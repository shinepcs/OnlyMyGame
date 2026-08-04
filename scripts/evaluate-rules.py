#!/usr/bin/env python3
"""Evaluate OnlyMyGame rule generation against the commercial release gates.

Live mode intentionally performs exactly 100 sequential API requests.  It is
guarded by both an explicit API origin and ONLYMYGAME_EVAL_CONFIRM so importing
this module, asking for help, or running --dry-run can never contact a service.
Only aggregate metrics and non-sensitive per-case measurements are persisted.
"""

from __future__ import annotations

import argparse
import copy
import datetime as dt
import hashlib
import http.client
import json
import math
import os
from pathlib import Path
import re
import secrets
import ssl
import sys
import tempfile
import time
from typing import Any, Iterable
import urllib.error
import urllib.parse
import urllib.request


CASE_COUNT = 100
MAP_RADIUS = 8
MAP_TILE_COUNT = 217
EXPECTED_API_VERSION = "v1"
EXPECTED_COMPATIBILITY_VERSION = "rules-v4-targeting-2026-08"
COMPATIBILITY_HEADER = "X-Rules-Compatibility"
CONFIRMATION_ENV = "ONLYMYGAME_EVAL_CONFIRM"
CONFIRMATION_VALUE = "RUN_100_PAID_REQUESTS"
FIRST_VALID_THRESHOLD = 0.95
REPAIRED_VALID_THRESHOLD = 0.99
P95_LATENCY_THRESHOLD_SECONDS = 8.0
UNIQUE_SIGNATURE_THRESHOLD = 0.80
MAX_RESPONSE_BYTES = 2_000_000

RESOURCE_TYPES = ("none", "food", "wood", "stone", "iron", "coin")
REAL_RESOURCES = RESOURCE_TYPES[1:]
FACTION_KINDS = ("player", "skeleton", "neutral")
BUILDING_TYPES = ("headquarters", "warehouse", "workshop", "watchtower", "market", "barracks")
EVENT_TYPES = ("turnStart", "turnEnd", "move", "attack", "kill", "gather", "build", "trade", "relationChanged", "tileEntered", "capture")
EFFECT_TYPES = ("resource", "sp", "relation", "status", "spawn", "unlockAction", "schedule", "factionSwitch", "typedState")
COMPARE_OPS = ("always", "equal", "greaterOrEqual", "lessOrEqual", "hasTag", "ownerIs")
COMMAND_TYPES = ("move", "gather", "hunt", "attack", "trade", "persuade", "hire", "build", "upgrade", "dynamic", "capture")
PROGRESS_KEYS = ("turn", "kills", "buildings", "coin", "territory", "alliances", "move", "gather", "hunt", "attack", "trade", "persuade", "hire", "build", "upgrade", "capture")
STATE_SCOPES = ("run", "turn", "faction", "unit", "building", "tile")
STATE_VALUE_TYPES = ("number", "boolean", "set")
NUMBER_EXPRESSION_OPS = ("constant", "state", "add", "subtract", "multiply", "divide", "countUnits", "countBuildings", "countTiles", "distance", "recentActionRatio")
PREDICATE_EXPRESSION_OPS = ("all", "any", "not", "numberEqual", "numberNotEqual", "numberGreater", "numberGreaterOrEqual", "numberLess", "numberLessOrEqual", "boolState", "setContains")
STATE_MUTATION_OPS = ("set", "add", "toggle", "setAdd", "setRemove")
DYNAMIC_TARGET_KINDS = ("none", "tile", "unit", "building")
DYNAMIC_TARGET_OWNERSHIPS = ("any", "player", "nonPlayer", "neutral")
DYNAMIC_TARGET_VISIBILITIES = ("visible", "explored")
DYNAMIC_BINDING_TOKENS = ("$actor", "$target", "$tile", "$owner")
MAX_AST_NODES = 256
MAX_AST_DEPTH = 4
MAX_STATE_SET_ELEMENTS = 32
MAX_RECENT_ACTION_TURNS = 6
MAX_DYNAMIC_TARGET_DISTANCE = 32
MAX_DYNAMIC_TARGET_CANDIDATES = 32
MAX_RULESET_ACTIONS = 3


class EvaluationError(Exception):
    """An expected failure whose message never contains response bodies or tokens."""


class NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Refuse redirects so an Authorization header cannot leave the chosen origin."""

    def redirect_request(self, req: Any, fp: Any, code: int, msg: str, headers: Any, newurl: str) -> None:
        return None


def utc_now_text() -> str:
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def stable_int(*parts: object) -> int:
    raw = "|".join(str(part) for part in parts).encode("utf-8")
    return int.from_bytes(hashlib.sha256(raw).digest()[:8], "big")


def canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def digest_json(value: Any) -> str:
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def make_resource_bag(case_index: int, faction_id: int) -> dict[str, int]:
    maximum = 30 + 10 * ((case_index + faction_id) % 3)
    values = {
        resource: 1 + stable_int("bag", case_index, faction_id, resource) % max(1, maximum - 1)
        for resource in REAL_RESOURCES
    }
    return {
        "food": values["food"],
        "wood": values["wood"],
        "stone": values["stone"],
        "iron": values["iron"],
        "coin": values["coin"],
        "maxFood": maximum,
        "maxWood": maximum,
        "maxStone": maximum,
        "maxIron": maximum,
        "maxCoin": maximum,
    }


def hex_coordinates(radius: int = MAP_RADIUS) -> list[tuple[int, int]]:
    coordinates: list[tuple[int, int]] = []
    for q in range(-radius, radius + 1):
        lower = max(-radius, -q - radius)
        upper = min(radius, -q + radius)
        for r in range(lower, upper + 1):
            coordinates.append((q, r))
    return coordinates


def make_map(seed: int) -> list[dict[str, Any]]:
    terrains = ("Grass", "Forest", "Hill", "Marsh", "Ruins")
    result: list[dict[str, Any]] = []
    for q, r in hex_coordinates():
        selector = stable_int("tile", seed, q, r)
        resource = RESOURCE_TYPES[selector % len(RESOURCE_TYPES)]
        distance = (abs(q) + abs(r) + abs(q + r)) // 2
        owner = 1 if distance <= 2 else 2 if q <= -4 else 3 if q >= 4 else 0
        result.append(
            {
                "position": {"q": q, "r": r},
                "terrain": terrains[(selector // 7) % len(terrains)],
                "resource": resource,
                "amount": 0 if resource == "none" else 1 + (selector // 31) % 9,
                "owner": owner,
                "explored": distance <= 5,
                "visible": distance <= 3,
            }
        )
    return result


def state_reference(scope: str, key: str, scope_id: str = "") -> dict[str, Any]:
    return {"scope": scope, "scopeId": scope_id, "key": key}


def number_expression(
    op: str,
    *,
    constant: int = 0,
    state: dict[str, Any] | None = None,
    left: dict[str, Any] | None = None,
    right: dict[str, Any] | None = None,
    selector: str = "",
    second_selector: str = "",
    action: str = "move",
    recent_turns: int = 1,
) -> dict[str, Any]:
    return {
        "op": op,
        "constant": constant,
        "state": state,
        "left": left,
        "right": right,
        "selector": selector,
        "secondSelector": second_selector,
        "action": action,
        "recentTurns": recent_turns,
    }


def predicate_expression(
    op: str,
    *,
    children: list[dict[str, Any]] | None = None,
    child: dict[str, Any] | None = None,
    left: dict[str, Any] | None = None,
    right: dict[str, Any] | None = None,
    state: dict[str, Any] | None = None,
    element: str = "",
) -> dict[str, Any]:
    return {
        "op": op,
        "children": [] if children is None else children,
        "child": child,
        "left": left,
        "right": right,
        "state": state,
        "element": element,
    }


def state_mutation(
    op: str,
    state: dict[str, Any],
    *,
    number_value: dict[str, Any] | None = None,
    bool_value: bool = False,
    set_values: list[str] | None = None,
    element: str = "",
) -> dict[str, Any]:
    return {
        "op": op,
        "state": state,
        "numberValue": number_value,
        "boolValue": bool_value,
        "setValues": [] if set_values is None else set_values,
        "element": element,
    }


def dynamic_target_selector(
    kind: str = "none",
    *,
    ownership: str = "any",
    visibility: str = "visible",
    min_distance: int = 0,
    max_distance: int = 0,
    max_candidates: int = 16,
) -> dict[str, Any]:
    return {
        "kind": kind,
        "ownership": ownership,
        "visibility": visibility,
        "minDistance": min_distance,
        "maxDistance": max_distance,
        "maxCandidates": max_candidates,
    }


def empty_condition(
    op: str = "always",
    *,
    left: str = "",
    value: int = 0,
    text: str = "",
    predicate: dict[str, Any] | None = None,
) -> dict[str, Any]:
    return {"op": op, "left": left, "value": value, "text": text, "all": [], "predicate": predicate}


def make_effect(case_index: int, rule_index: int) -> dict[str, Any]:
    variant = rule_index % 4
    base = {"resource": "none", "amount": 0, "target": "", "key": "", "value": "", "delay": 0, "stateMutation": None}
    if variant == 0:
        base.update(type="resource", resource=REAL_RESOURCES[(case_index + rule_index) % len(REAL_RESOURCES)], amount=1 + case_index % 3)
    elif variant == 1:
        base.update(type="sp", amount=1)
    elif variant == 2:
        base.update(type="relation", amount=1 + case_index % 4)
    else:
        base.update(type="status", key=f"omen-{rule_index}", amount=1 + case_index % 7)
    return base


def make_active_rule(case_index: int, rule_index: int, turn: int) -> dict[str, Any]:
    conditions = (
        empty_condition(),
        empty_condition("greaterOrEqual", left="luck", value=10 + case_index % 60),
        empty_condition("ownerIs", left="player_tile", value=1),
        empty_condition("hasTag", left="player", text="explorer"),
    )
    return {
        "id": f"existing-rule-{case_index:03d}-{rule_index}",
        "name": f"Representative Rule {rule_index}",
        "description": "Deterministic evaluation state.",
        "trigger": EVENT_TYPES[(case_index + rule_index) % len(EVENT_TYPES)],
        "condition": copy.deepcopy(conditions[rule_index % len(conditions)]),
        "effects": [make_effect(case_index, rule_index)],
        "stateDefinitions": [],
        "priority": rule_index - 2,
        "durationTurns": 30,
        "appliedTurn": max(0, turn - rule_index - 1),
        "worldCue": "Evaluation cue",
    }


def make_dynamic_action(case_index: int, action_index: int, turn: int) -> dict[str, Any]:
    selector = dynamic_target_selector()
    return {
        "id": f"existing-action-{case_index:03d}-{action_index}",
        "name": f"Representative Action {action_index}",
        "description": "Deterministic evaluation action.",
        "spCost": 1 + action_index,
        "resourceCost": "none",
        "resourceAmount": 0,
        "cooldown": 2 + action_index,
        "availableTurn": max(0, turn - action_index),
        "targetSelector": selector,
        "condition": empty_condition(),
        "effects": [
            {
                "type": "resource",
                "resource": REAL_RESOURCES[(case_index + action_index + 2) % len(REAL_RESOURCES)],
                "amount": 1,
                "target": "",
                "key": "",
                "value": "",
                "delay": 0,
                "stateMutation": None,
            }
        ],
    }


def action_count(case_index: int, command_index: int) -> int:
    # Keep every case's cumulative command profile distinct while remaining far
    # below RuleLimits.MaxStateMagnitude.
    return case_index * (command_index + 3) + command_index * 5


def progress_value(progress_key: str, turn: int, player_kills: int, player_coin: int, player_buildings: int, stats: dict[str, int]) -> int:
    if progress_key == "turn":
        return turn
    if progress_key == "kills":
        return player_kills
    if progress_key == "buildings":
        return player_buildings
    if progress_key == "coin":
        return player_coin
    return stats.get(progress_key, 0)


def make_contract(
    case_index: int,
    contract_index: int,
    turn: int,
    player_kills: int,
    player_coin: int,
    player_buildings: int,
    stats: dict[str, int],
) -> dict[str, Any]:
    progress_key = PROGRESS_KEYS[(case_index + contract_index * 5) % len(PROGRESS_KEYS)]
    current = progress_value(progress_key, turn, player_kills, player_coin, player_buildings, stats)
    announced = max(0, turn - 5 - contract_index)
    increment = 3 if progress_key == "turn" else 4 if progress_key == "coin" else 2
    return {
        "id": f"existing-contract-{case_index:03d}-{contract_index}",
        "title": f"Representative Contract {contract_index}",
        "description": "A reachable deterministic evaluation contract.",
        "progressKey": progress_key,
        "target": current + increment,
        "minimumTurns": 18 + contract_index,
        "announcedTurn": announced,
        "achievableFromTurn": announced + 2,
        "replaceWarningTurn": 0,
        "worldCue": "Evaluation objective",
    }


def make_snapshot(case_index: int, run_id: str) -> dict[str, Any]:
    seed = 2_026_080_500 + case_index
    turn = 3 + (case_index * 11) % 28
    player_kills = (case_index * 3) % 17
    stats = {command: action_count(case_index, index) for index, command in enumerate(COMMAND_TYPES)}
    factions = [
        {
            "id": faction_id,
            "name": ("Expedition", "Skeleton Court", "Free Traders")[faction_id - 1],
            "kind": FACTION_KINDS[faction_id - 1],
            "resources": make_resource_bag(case_index, faction_id),
            "maxSp": 6 + (case_index + faction_id) % 7,
            "sp": 3 + (case_index * faction_id) % (4 + (case_index + faction_id) % 7),
            "relationToPlayer": 0 if faction_id == 1 else -60 + (case_index * (17 + faction_id)) % 121,
        }
        for faction_id in (1, 2, 3)
    ]
    # Keep SP within max after varying both independently.
    for faction in factions:
        faction["sp"] = min(faction["sp"], faction["maxSp"])

    buildings = [
        {"id": 1, "factionId": 1, "position": {"q": 0, "r": 0}, "type": "headquarters", "level": 1, "hp": 12},
        {"id": 2, "factionId": 2, "position": {"q": -6, "r": 1}, "type": "headquarters", "level": 1, "hp": 12},
        {"id": 3, "factionId": 3, "position": {"q": 4, "r": -4}, "type": "headquarters", "level": 1, "hp": 12},
        {
            "id": 4,
            "factionId": 1,
            "position": {"q": 1, "r": -1},
            "type": BUILDING_TYPES[1 + case_index % (len(BUILDING_TYPES) - 1)],
            "level": 1 + case_index % 3,
            "hp": 8 + case_index % 5,
        },
    ]
    action_stats = [{"type": command, "count": count} for command, count in stats.items()]
    active_rules = [make_active_rule(case_index, index, turn) for index in range(1 + case_index % 4)]
    dynamic_actions = [make_dynamic_action(case_index, index, turn) for index in range(case_index % 3)]
    player_coin = factions[0]["resources"]["coin"]
    contracts = [
        make_contract(case_index, index, turn, player_kills, player_coin, 2, stats)
        for index in range(1 + case_index % 3)
    ]
    return {
        "runId": run_id,
        "turn": turn,
        "seed": seed,
        "luck": 1 + (case_index * 37) % 100,
        "playerKills": player_kills,
        "outcome": "ongoing",
        "phase": "awaitingRules",
        "completedContractId": "",
        "planningPrepared": False,
        "map": make_map(seed),
        "entities": [
            {"id": 1, "factionId": 1, "position": {"q": 0, "r": 1}, "hp": 5, "speed": 2, "alive": True, "tags": ["explorer", "worker"]},
            {"id": 2, "factionId": 1, "position": {"q": 1, "r": 0}, "hp": 4 + case_index % 3, "speed": 2, "alive": True, "tags": ["guard"]},
            {"id": 3, "factionId": 2, "position": {"q": -5, "r": 0}, "hp": 5, "speed": 2, "alive": True, "tags": ["undead"]},
            {"id": 4, "factionId": 2, "position": {"q": -5, "r": 1}, "hp": 3 + case_index % 4, "speed": 1, "alive": True, "tags": ["raider"]},
            {"id": 5, "factionId": 3, "position": {"q": 3, "r": -3}, "hp": 5, "speed": 2, "alive": True, "tags": ["merchant"]},
            {"id": 6, "factionId": 3, "position": {"q": 4, "r": -3}, "hp": 5, "speed": 2, "alive": True, "tags": ["scout"]},
        ],
        "buildings": buildings,
        "factions": factions,
        "actionStats": action_stats,
        "activeRules": active_rules,
        "victoryContracts": contracts,
        "dynamicActions": dynamic_actions,
        "ruleState": [
            {"key": "omen", "value": case_index % 11},
            {"key": "season", "value": (case_index * 7) % 19},
        ],
        "typedRuleState": [
            {
                "scope": "run",
                "scopeId": "",
                "key": "evaluation_omen",
                "valueType": "number",
                "koreanName": "평가 징조",
                "iconToken": "evaluation_omen",
                "colorHex": "#33AAFF",
                "numberValue": case_index % 17,
                "boolValue": False,
                "setValue": [],
                "stateTurn": 0,
            }
        ],
        "recentActionStats": [
            {
                "turn": max(0, turn - offset),
                "type": COMMAND_TYPES[(case_index + offset) % len(COMMAND_TYPES)],
                "count": 1 + (case_index + offset) % 5,
            }
            for offset in range(3)
        ],
        "ruleBudget": {
            "turn": turn,
            "dispatches": case_index % 5,
            "conditionWork": (case_index * 17) % 2_000,
            "activations": case_index % 7,
            "effects": case_index % 11,
            "spawnedEntities": 0,
            "loggedLimits": 0,
        },
        "journal": ["Representative evaluation state", f"Case {case_index:03d}"],
        "catalogHash": "kaykit-v1",
    }


def build_dataset(run_id: str) -> list[dict[str, Any]]:
    return [make_snapshot(index, run_id) for index in range(CASE_COUNT)]


def dataset_digest(dataset: Iterable[dict[str, Any]]) -> str:
    scrubbed = []
    for snapshot in dataset:
        item = copy.deepcopy(snapshot)
        item["runId"] = "<ephemeral-run-id>"
        scrubbed.append(item)
    return digest_json(scrubbed)


def validate_dataset(dataset: list[dict[str, Any]]) -> dict[str, Any]:
    errors: list[str] = []
    if len(dataset) != CASE_COUNT:
        errors.append("DATASET_CASE_COUNT")
    run_ids = {snapshot.get("runId") for snapshot in dataset}
    seeds = {snapshot.get("seed") for snapshot in dataset}
    active_counts = {len(snapshot.get("activeRules", [])) for snapshot in dataset}
    contract_counts = {len(snapshot.get("victoryContracts", [])) for snapshot in dataset}
    resource_profiles = {digest_json(snapshot.get("factions")) for snapshot in dataset}
    stat_profiles = {digest_json(snapshot.get("actionStats")) for snapshot in dataset}
    terrains: set[str] = set()
    resources: set[str] = set()
    maximum_bytes = 0
    for index, snapshot in enumerate(dataset):
        tiles = snapshot.get("map")
        if not isinstance(tiles, list) or len(tiles) != MAP_TILE_COUNT:
            errors.append(f"CASE_{index:03d}_MAP_COUNT")
            continue
        coordinates: set[tuple[int, int]] = set()
        for tile in tiles:
            position = tile.get("position", {}) if isinstance(tile, dict) else {}
            q, r = position.get("q"), position.get("r")
            if not isinstance(q, int) or isinstance(q, bool) or not isinstance(r, int) or isinstance(r, bool):
                errors.append(f"CASE_{index:03d}_MAP_COORDINATE")
                break
            coordinates.add((q, r))
            if max(abs(q), abs(r), abs(q + r)) > MAP_RADIUS:
                errors.append(f"CASE_{index:03d}_MAP_RADIUS")
                break
            terrains.add(str(tile.get("terrain")))
            resources.add(str(tile.get("resource")))
        if len(coordinates) != MAP_TILE_COUNT:
            errors.append(f"CASE_{index:03d}_MAP_UNIQUE_COORDINATES")
        factions = snapshot.get("factions")
        if not isinstance(factions, list) or len(factions) != 3 or {faction.get("kind") for faction in factions} != set(FACTION_KINDS):
            errors.append(f"CASE_{index:03d}_FACTIONS")
        if not snapshot.get("activeRules") or not snapshot.get("victoryContracts"):
            errors.append(f"CASE_{index:03d}_RULE_OR_CONTRACT_EMPTY")
        maximum_bytes = max(maximum_bytes, len(canonical_json(snapshot).encode("utf-8")))
    if len(run_ids) != 1:
        errors.append("ONE_RUN_ID_REQUIRED")
    if len(seeds) != CASE_COUNT:
        errors.append("SEEDS_NOT_UNIQUE")
    if active_counts != {1, 2, 3, 4}:
        errors.append("ACTIVE_RULE_DIVERSITY")
    if contract_counts != {1, 2, 3}:
        errors.append("CONTRACT_DIVERSITY")
    if len(resource_profiles) < 90 or len(stat_profiles) < 90:
        errors.append("STATE_PROFILE_DIVERSITY")
    if len(terrains) < 5 or set(RESOURCE_TYPES) - resources:
        errors.append("MAP_CONTENT_DIVERSITY")
    if maximum_bytes > 1_000_000:
        errors.append("SNAPSHOT_REQUEST_SIZE")
    if errors:
        raise EvaluationError("dataset self-check failed: " + ",".join(errors[:12]))
    return {
        "caseCount": len(dataset),
        "mapRadius": MAP_RADIUS,
        "mapTilesPerCase": MAP_TILE_COUNT,
        "factionsPerCase": 3,
        "uniqueSeeds": len(seeds),
        "activeRuleCountVariants": sorted(active_counts),
        "contractCountVariants": sorted(contract_counts),
        "uniqueResourceProfiles": len(resource_profiles),
        "uniqueActionStatProfiles": len(stat_profiles),
        "maximumSnapshotBytes": maximum_bytes,
        "datasetSha256": dataset_digest(dataset),
    }


def exact_keys(value: Any, keys: set[str], path: str, errors: list[str]) -> bool:
    if not isinstance(value, dict):
        errors.append(path + ":OBJECT_REQUIRED")
        return False
    if set(value) != keys:
        errors.append(path + ":FIELDS_INVALID")
        return False
    return True


def is_int(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def bounded_string(value: Any, maximum: int, allow_empty: bool = True) -> bool:
    return isinstance(value, str) and len(value) <= maximum and (allow_empty or bool(value.strip()))


def identifier(value: Any) -> bool:
    return bounded_string(value, 64, allow_empty=False) and all(character.isprintable() for character in value)


def enter_ast(path: str, errors: list[str], depth: int, counter: list[int]) -> bool:
    counter[0] += 1
    if depth > MAX_AST_DEPTH or counter[0] > MAX_AST_NODES:
        errors.append(path + ":AST_LIMIT")
        return False
    return True


def looks_like_binding(value: Any) -> bool:
    return isinstance(value, str) and value.startswith("$")


def uses_selected_target_binding(value: Any) -> bool:
    if isinstance(value, str):
        return value in ("$target", "$tile", "$owner")
    if isinstance(value, list):
        return any(uses_selected_target_binding(item) for item in value)
    if isinstance(value, dict):
        return any(uses_selected_target_binding(item) for item in value.values())
    return False


def validate_dynamic_target_selector(value: Any, path: str, errors: list[str]) -> bool:
    keys = {"kind", "ownership", "visibility", "minDistance", "maxDistance", "maxCandidates"}
    if not exact_keys(value, keys, path, errors):
        return False
    if value["kind"] not in DYNAMIC_TARGET_KINDS or value["ownership"] not in DYNAMIC_TARGET_OWNERSHIPS or value["visibility"] not in DYNAMIC_TARGET_VISIBILITIES:
        errors.append(path + ":ENUM_INVALID")
        return False
    if not all(is_int(value[field]) for field in ("minDistance", "maxDistance", "maxCandidates")):
        errors.append(path + ":NUMBER_INVALID")
        return False
    if not 0 <= value["minDistance"] <= value["maxDistance"] <= MAX_DYNAMIC_TARGET_DISTANCE or not 1 <= value["maxCandidates"] <= MAX_DYNAMIC_TARGET_CANDIDATES:
        errors.append(path + ":BOUNDS_INVALID")
    if value["kind"] == "none" and (value["ownership"] != "any" or value["visibility"] != "visible" or value["minDistance"] != 0 or value["maxDistance"] != 0 or value["maxCandidates"] != 16):
        errors.append(path + ":NONE_DEFAULT_INVALID")
    if value["kind"] in ("unit", "building") and value["visibility"] != "visible":
        errors.append(path + ":ENTITY_VISIBILITY_INVALID")
    return True


def number_binding_allowed(op: str, token: str, target_selector: dict[str, Any] | None) -> bool:
    if target_selector is None or target_selector.get("kind") == "none" or token not in DYNAMIC_BINDING_TOKENS:
        return False
    if token == "$actor":
        return op in ("countUnits", "distance")
    if token == "$tile":
        return op in ("countTiles", "distance")
    if token == "$owner":
        return target_selector.get("visibility") == "visible" and op in ("countUnits", "countBuildings", "countTiles")
    return op == "distance" or op == "countUnits" and target_selector.get("kind") == "unit" or op == "countBuildings" and target_selector.get("kind") == "building" or op == "countTiles" and target_selector.get("kind") == "tile"


def validate_state_reference(value: Any, path: str, errors: list[str]) -> None:
    keys = {"scope", "scopeId", "key"}
    if not exact_keys(value, keys, path, errors):
        return
    if value["scope"] not in STATE_SCOPES or not bounded_string(value["scopeId"], 64) or not identifier(value["key"]) or looks_like_binding(value["scopeId"]) or looks_like_binding(value["key"]):
        errors.append(path + ":VALUE_INVALID")


def validate_state_definition(value: Any, path: str, errors: list[str]) -> None:
    keys = {
        "scope", "scopeId", "key", "valueType", "koreanName", "iconToken", "colorHex",
        "initialNumber", "initialBool", "initialSet",
    }
    if not exact_keys(value, keys, path, errors):
        return
    if value["scope"] not in STATE_SCOPES or value["valueType"] not in STATE_VALUE_TYPES:
        errors.append(path + ":ENUM_INVALID")
    if not bounded_string(value["scopeId"], 64) or not identifier(value["key"]):
        errors.append(path + ":IDENTITY_INVALID")
    korean_name = value["koreanName"]
    if not bounded_string(korean_name, 80, allow_empty=False) or not any("가" <= character <= "힣" for character in korean_name):
        errors.append(path + ":KOREAN_NAME_INVALID")
    icon = value["iconToken"]
    if not identifier(icon) or not all(character.isalnum() or character in "-_" for character in icon):
        errors.append(path + ":ICON_INVALID")
    if not isinstance(value["colorHex"], str) or re.fullmatch(r"#[0-9A-Fa-f]{6}", value["colorHex"]) is None:
        errors.append(path + ":COLOR_INVALID")
    if not is_int(value["initialNumber"]) or not -1_000_000 <= value["initialNumber"] <= 1_000_000 or not isinstance(value["initialBool"], bool):
        errors.append(path + ":INITIAL_VALUE_INVALID")
    initial_set = value["initialSet"]
    if not isinstance(initial_set, list) or len(initial_set) > MAX_STATE_SET_ELEMENTS or not all(identifier(item) for item in initial_set) or len(set(initial_set)) != len(initial_set):
        errors.append(path + ":INITIAL_SET_INVALID")


def validate_number_expression(value: Any, path: str, errors: list[str], depth: int, counter: list[int], target_selector: dict[str, Any] | None = None) -> None:
    keys = {"op", "constant", "state", "left", "right", "selector", "secondSelector", "action", "recentTurns"}
    if not exact_keys(value, keys, path, errors) or not enter_ast(path, errors, depth, counter):
        return
    op = value["op"]
    if op not in NUMBER_EXPRESSION_OPS:
        errors.append(path + ":OP_INVALID")
    if not is_int(value["constant"]) or not -1_000_000 <= value["constant"] <= 1_000_000:
        errors.append(path + ":CONSTANT_INVALID")
    if not bounded_string(value["selector"], 64) or not bounded_string(value["secondSelector"], 64):
        errors.append(path + ":SELECTOR_INVALID")
    if looks_like_binding(value["selector"]) and not number_binding_allowed(op, value["selector"], target_selector):
        errors.append(path + ":BINDING_POSITION_INVALID")
    if looks_like_binding(value["secondSelector"]) and (op != "distance" or not number_binding_allowed(op, value["secondSelector"], target_selector)):
        errors.append(path + ":BINDING_POSITION_INVALID")
    if value["action"] not in COMMAND_TYPES or not is_int(value["recentTurns"]) or not 1 <= value["recentTurns"] <= MAX_RECENT_ACTION_TURNS:
        errors.append(path + ":RECENT_ACTION_INVALID")

    if value["state"] is not None:
        validate_state_reference(value["state"], path + ".state", errors)
    for field in ("left", "right"):
        child = value[field]
        if child is not None:
            validate_number_expression(child, path + "." + field, errors, depth + 1, counter, target_selector)

    if op == "state" and value["state"] is None:
        errors.append(path + ":STATE_REQUIRED")
    if op in ("add", "subtract", "multiply", "divide") and (value["left"] is None or value["right"] is None):
        errors.append(path + ":OPERANDS_REQUIRED")
    if op == "distance" and (not value["selector"] or not value["secondSelector"]):
        errors.append(path + ":DISTANCE_SELECTORS_REQUIRED")


def validate_predicate_expression(value: Any, path: str, errors: list[str], depth: int, counter: list[int], target_selector: dict[str, Any] | None = None) -> None:
    keys = {"op", "children", "child", "left", "right", "state", "element"}
    if not exact_keys(value, keys, path, errors) or not enter_ast(path, errors, depth, counter):
        return
    op = value["op"]
    if op not in PREDICATE_EXPRESSION_OPS:
        errors.append(path + ":OP_INVALID")
    children = value["children"]
    if not isinstance(children, list) or len(children) > MAX_AST_NODES:
        errors.append(path + ":CHILDREN_INVALID")
        children = []
    if not bounded_string(value["element"], 64):
        errors.append(path + ":ELEMENT_INVALID")
    if looks_like_binding(value["element"]):
        errors.append(path + ":BINDING_POSITION_INVALID")

    for index, child in enumerate(children):
        validate_predicate_expression(child, f"{path}.children[{index}]", errors, depth + 1, counter, target_selector)
    if value["child"] is not None:
        validate_predicate_expression(value["child"], path + ".child", errors, depth + 1, counter, target_selector)
    for field in ("left", "right"):
        expression = value[field]
        if expression is not None:
            validate_number_expression(expression, path + "." + field, errors, depth + 1, counter, target_selector)
    if value["state"] is not None:
        validate_state_reference(value["state"], path + ".state", errors)

    if op in ("all", "any") and not children:
        errors.append(path + ":CHILDREN_REQUIRED")
    elif op == "not" and value["child"] is None:
        errors.append(path + ":CHILD_REQUIRED")
    elif op.startswith("number") and (value["left"] is None or value["right"] is None):
        errors.append(path + ":NUMBER_OPERANDS_REQUIRED")
    elif op in ("boolState", "setContains") and value["state"] is None:
        errors.append(path + ":STATE_REQUIRED")
    if op == "setContains" and not identifier(value["element"]):
        errors.append(path + ":SET_ELEMENT_INVALID")


def validate_state_mutation(value: Any, path: str, errors: list[str], depth: int, counter: list[int], target_selector: dict[str, Any] | None = None) -> None:
    keys = {"op", "state", "numberValue", "boolValue", "setValues", "element"}
    if not exact_keys(value, keys, path, errors) or not enter_ast(path, errors, depth, counter):
        return
    op = value["op"]
    if op not in STATE_MUTATION_OPS:
        errors.append(path + ":OP_INVALID")
    validate_state_reference(value["state"], path + ".state", errors)
    if value["numberValue"] is not None:
        validate_number_expression(value["numberValue"], path + ".numberValue", errors, depth + 1, counter, target_selector)
    if not isinstance(value["boolValue"], bool):
        errors.append(path + ":BOOL_INVALID")
    set_values = value["setValues"]
    if not isinstance(set_values, list) or len(set_values) > MAX_STATE_SET_ELEMENTS or not all(identifier(item) for item in set_values) or len(set(set_values)) != len(set_values):
        errors.append(path + ":SET_VALUES_INVALID")
    if not bounded_string(value["element"], 64):
        errors.append(path + ":ELEMENT_INVALID")
    if looks_like_binding(value["element"]) or isinstance(set_values, list) and any(looks_like_binding(item) for item in set_values):
        errors.append(path + ":BINDING_POSITION_INVALID")
    if op == "add" and value["numberValue"] is None:
        errors.append(path + ":NUMBER_VALUE_REQUIRED")
    if op in ("setAdd", "setRemove") and not identifier(value["element"]):
        errors.append(path + ":SET_ELEMENT_INVALID")


def validate_condition(condition: Any, path: str, errors: list[str], depth: int = 1, counter: list[int] | None = None, target_selector: dict[str, Any] | None = None) -> None:
    if counter is None:
        counter = [0]
    keys = {"op", "left", "value", "text", "all", "predicate"}
    if not exact_keys(condition, keys, path, errors) or not enter_ast(path, errors, depth, counter):
        return
    if condition["op"] not in COMPARE_OPS or not bounded_string(condition["left"], 64) or not is_int(condition["value"]) or not bounded_string(condition["text"], 64):
        errors.append(path + ":VALUE_INVALID")
    op = condition["op"]
    if op == "hasTag":
        allowed = condition["left"] == "$actor" and target_selector is not None and target_selector.get("kind") != "none" or condition["left"] == "$target" and target_selector is not None and target_selector.get("kind") == "unit"
        if looks_like_binding(condition["left"]) and not allowed or looks_like_binding(condition["text"]):
            errors.append(path + ":BINDING_POSITION_INVALID")
    elif op == "ownerIs":
        selector_value = condition["left"] or condition["text"]
        if looks_like_binding(selector_value) and not (selector_value == "$tile" and target_selector is not None and target_selector.get("kind") != "none"):
            errors.append(path + ":BINDING_POSITION_INVALID")
    elif looks_like_binding(condition["left"]) or looks_like_binding(condition["text"]):
        errors.append(path + ":BINDING_POSITION_INVALID")
    children = condition["all"]
    if not isinstance(children, list) or len(children) > MAX_AST_NODES:
        errors.append(path + ":ALL_REQUIRED")
        children = []
    for index, child in enumerate(children):
        validate_condition(child, f"{path}.all[{index}]", errors, depth + 1, counter, target_selector)
    if condition["predicate"] is not None:
        validate_predicate_expression(condition["predicate"], path + ".predicate", errors, depth + 1, counter, target_selector)


def validate_effect(effect: Any, path: str, errors: list[str], counter: list[int] | None = None, target_selector: dict[str, Any] | None = None) -> None:
    keys = {"type", "resource", "amount", "target", "key", "value", "delay", "stateMutation"}
    if not exact_keys(effect, keys, path, errors):
        return
    if effect["type"] not in EFFECT_TYPES or effect["resource"] not in RESOURCE_TYPES:
        errors.append(path + ":ENUM_INVALID")
        return
    if not is_int(effect["amount"]) or not is_int(effect["delay"]):
        errors.append(path + ":NUMBER_INVALID")
        return
    if not all(bounded_string(effect[field], 600 if field == "value" else 64) for field in ("target", "key", "value")):
        errors.append(path + ":TEXT_INVALID")
    effect_type = effect["type"]
    amount = effect["amount"]
    if looks_like_binding(effect["key"]) or looks_like_binding(effect["value"]):
        errors.append(path + ":BINDING_POSITION_INVALID")
    if looks_like_binding(effect["target"]):
        binding_allowed = target_selector is not None and target_selector.get("kind") != "none" and (
            effect_type == "factionSwitch" and effect["target"] == "$target" and target_selector.get("kind") == "unit" or
            effect_type in ("spawn", "relation") and effect["target"] == "$owner" and target_selector.get("visibility") == "visible"
        )
        if not binding_allowed:
            errors.append(path + ":BINDING_POSITION_INVALID")
    if effect_type == "resource" and (effect["resource"] == "none" or not 1 <= amount <= 1_000):
        errors.append(path + ":RESOURCE_INVALID")
    elif effect_type == "sp" and (amount == 0 or not -10 <= amount <= 10):
        errors.append(path + ":SP_INVALID")
    elif effect_type == "relation" and (amount == 0 or not -100 <= amount <= 100):
        errors.append(path + ":RELATION_INVALID")
    elif effect_type == "spawn" and not 1 <= amount <= 4:
        errors.append(path + ":SPAWN_INVALID")
    elif effect_type == "unlockAction" and (not effect["key"] or not 1 <= amount <= 10):
        errors.append(path + ":UNLOCK_INVALID")
    elif effect_type == "schedule" and (effect["key"] not in EVENT_TYPES or effect["resource"] == "none" or not 1 <= amount <= 1_000 or not 1 <= effect["delay"] <= 30):
        errors.append(path + ":SCHEDULE_INVALID")
    elif effect_type == "factionSwitch" and effect["target"] != "$target" and (not effect["target"].isdigit() or not effect["key"].isdigit()):
        errors.append(path + ":FACTION_SWITCH_INVALID")
    if effect["stateMutation"] is not None:
        validate_state_mutation(effect["stateMutation"], path + ".stateMutation", errors, 2, counter or [0], target_selector)
    if effect_type == "typedState" and effect["stateMutation"] is None:
        errors.append(path + ":STATE_MUTATION_REQUIRED")


def validate_rule_set_response(value: Any, request_id: str, apply_turn: int) -> list[str]:
    errors: list[str] = []
    root_keys = {"schemaVersion", "requestId", "applyTurn", "koreanSummary", "changes", "actions", "victoryContracts"}
    if not exact_keys(value, root_keys, "root", errors):
        return errors
    if value["schemaVersion"] != EXPECTED_API_VERSION or value["requestId"] != request_id or value["applyTurn"] != apply_turn:
        errors.append("root:IDENTITY_INVALID")
    if not bounded_string(value["koreanSummary"], 600, allow_empty=False):
        errors.append("root:SUMMARY_INVALID")
    changes, actions, contracts = value["changes"], value["actions"], value["victoryContracts"]
    if not isinstance(changes, list) or not 1 <= len(changes) <= 3:
        errors.append("changes:COUNT_INVALID")
        changes = []
    if not isinstance(actions, list) or len(actions) > MAX_RULESET_ACTIONS:
        errors.append("actions:COUNT_INVALID")
        actions = []
    if not isinstance(contracts, list) or len(contracts) > 3:
        errors.append("victoryContracts:COUNT_INVALID")
        contracts = []

    seen_rule_ids: set[str] = set()
    for index, rule in enumerate(changes):
        path = f"changes[{index}]"
        keys = {"id", "name", "description", "trigger", "condition", "effects", "stateDefinitions", "priority", "durationTurns", "appliedTurn", "worldCue"}
        if not exact_keys(rule, keys, path, errors):
            continue
        if not bounded_string(rule["id"], 64, False) or rule["id"] in seen_rule_ids:
            errors.append(path + ":ID_INVALID")
        else:
            seen_rule_ids.add(rule["id"])
        if not bounded_string(rule["name"], 80, False) or not bounded_string(rule["description"], 600, False) or not bounded_string(rule["worldCue"], 80):
            errors.append(path + ":TEXT_INVALID")
        if rule["trigger"] not in EVENT_TYPES or not is_int(rule["priority"]) or not -1_000 <= rule["priority"] <= 1_000 or not is_int(rule["durationTurns"]) or not 1 <= rule["durationTurns"] <= 30 or rule["appliedTurn"] != apply_turn:
            errors.append(path + ":BOUNDS_INVALID")
        ast_counter = [0]
        validate_condition(rule["condition"], path + ".condition", errors, counter=ast_counter)
        definitions = rule["stateDefinitions"]
        if not isinstance(definitions, list) or len(definitions) > 4:
            errors.append(path + ":STATE_DEFINITION_COUNT")
            definitions = []
        definition_identities: set[tuple[Any, Any, Any]] = set()
        for definition_index, definition in enumerate(definitions):
            validate_state_definition(definition, f"{path}.stateDefinitions[{definition_index}]", errors)
            if isinstance(definition, dict):
                identity = (definition.get("scope"), definition.get("scopeId"), definition.get("key"))
                if identity in definition_identities:
                    errors.append(path + ":STATE_DEFINITION_DUPLICATE")
                definition_identities.add(identity)
        effects = rule["effects"]
        if not isinstance(effects, list) or not 1 <= len(effects) <= 16:
            errors.append(path + ":EFFECT_COUNT")
        else:
            for effect_index, effect in enumerate(effects):
                validate_effect(effect, f"{path}.effects[{effect_index}]", errors, ast_counter)

    seen_action_ids: set[str] = set()
    for index, action in enumerate(actions):
        path = f"actions[{index}]"
        keys = {"id", "name", "description", "spCost", "resourceCost", "resourceAmount", "cooldown", "availableTurn", "targetSelector", "condition", "effects"}
        if not exact_keys(action, keys, path, errors):
            continue
        if not bounded_string(action["id"], 64, False) or action["id"] in seen_action_ids:
            errors.append(path + ":ID_INVALID")
        else:
            seen_action_ids.add(action["id"])
        numeric_fields = ("spCost", "resourceAmount", "cooldown", "availableTurn")
        if not all(is_int(action[field]) for field in numeric_fields) or not 0 <= action["spCost"] <= 10 or not 0 <= action["resourceAmount"] <= 1_000 or not 0 <= action["cooldown"] <= 30 or not apply_turn <= action["availableTurn"] <= apply_turn + 30:
            errors.append(path + ":BOUNDS_INVALID")
        if action["resourceCost"] not in RESOURCE_TYPES or not bounded_string(action["name"], 80, False) or not bounded_string(action["description"], 600, False):
            errors.append(path + ":VALUE_INVALID")
        selector_valid = validate_dynamic_target_selector(action["targetSelector"], path + ".targetSelector", errors)
        target_selector = action["targetSelector"] if selector_valid else None
        if target_selector is not None and target_selector["kind"] != "none" and not uses_selected_target_binding({"condition": action["condition"], "effects": action["effects"]}):
            errors.append(path + ":DYNAMIC_TARGET_UNUSED")
        ast_counter = [0]
        validate_condition(action["condition"], path + ".condition", errors, counter=ast_counter, target_selector=target_selector)
        effects = action["effects"]
        if not isinstance(effects, list) or not 1 <= len(effects) <= 16:
            errors.append(path + ":EFFECT_COUNT")
        else:
            for effect_index, effect in enumerate(effects):
                validate_effect(effect, f"{path}.effects[{effect_index}]", errors, ast_counter, target_selector)

    seen_contract_ids: set[str] = set()
    for index, contract in enumerate(contracts):
        path = f"victoryContracts[{index}]"
        keys = {"id", "title", "description", "progressKey", "target", "minimumTurns", "announcedTurn", "achievableFromTurn", "replaceWarningTurn", "worldCue"}
        if not exact_keys(contract, keys, path, errors):
            continue
        if not bounded_string(contract["id"], 64, False) or contract["id"] in seen_contract_ids:
            errors.append(path + ":ID_INVALID")
        else:
            seen_contract_ids.add(contract["id"])
        numbers = ("target", "minimumTurns", "announcedTurn", "achievableFromTurn", "replaceWarningTurn")
        if not all(is_int(contract[field]) for field in numbers) or not 1 <= contract["target"] <= 1_000_000 or not 3 <= contract["minimumTurns"] <= 30 or any(not 0 <= contract[field] <= 1_000_000 for field in ("announcedTurn", "achievableFromTurn", "replaceWarningTurn")):
            errors.append(path + ":BOUNDS_INVALID")
        if contract["progressKey"] not in PROGRESS_KEYS or not bounded_string(contract["title"], 80, False) or not bounded_string(contract["description"], 600, False) or not bounded_string(contract["worldCue"], 80):
            errors.append(path + ":VALUE_INVALID")
    return errors[:20]


def canonical_state_reference(reference: dict[str, Any]) -> dict[str, Any]:
    return {"scope": reference["scope"], "scopeId": reference["scopeId"], "key": reference["key"]}


def canonical_state_definition(definition: dict[str, Any]) -> dict[str, Any]:
    result = {
        "scope": definition["scope"],
        "scopeId": definition["scopeId"],
        "key": definition["key"],
        "valueType": definition["valueType"],
    }
    if definition["valueType"] == "number":
        result["initial"] = definition["initialNumber"]
    elif definition["valueType"] == "boolean":
        result["initial"] = definition["initialBool"]
    else:
        result["initial"] = sorted(definition["initialSet"])
    return result


def canonical_number_expression(expression: dict[str, Any]) -> dict[str, Any]:
    op = expression["op"]
    result: dict[str, Any] = {"op": op}
    if op == "constant":
        result["constant"] = expression["constant"]
    elif op == "state":
        result["state"] = canonical_state_reference(expression["state"])
    elif op in ("add", "subtract", "multiply", "divide"):
        result["left"] = canonical_number_expression(expression["left"])
        result["right"] = canonical_number_expression(expression["right"])
    elif op in ("countUnits", "countBuildings", "countTiles"):
        result["selector"] = expression["selector"]
    elif op == "distance":
        result["selector"] = expression["selector"]
        result["secondSelector"] = expression["secondSelector"]
    elif op == "recentActionRatio":
        result["action"] = expression["action"]
        result["recentTurns"] = expression["recentTurns"]
    return result


def canonical_predicate_expression(predicate: dict[str, Any]) -> dict[str, Any]:
    op = predicate["op"]
    result: dict[str, Any] = {"op": op}
    if op in ("all", "any"):
        result["children"] = sorted((canonical_predicate_expression(child) for child in predicate["children"]), key=canonical_json)
    elif op == "not":
        result["child"] = canonical_predicate_expression(predicate["child"])
    elif op.startswith("number"):
        result["left"] = canonical_number_expression(predicate["left"])
        result["right"] = canonical_number_expression(predicate["right"])
    elif op == "boolState":
        result["state"] = canonical_state_reference(predicate["state"])
    elif op == "setContains":
        result["state"] = canonical_state_reference(predicate["state"])
        result["element"] = predicate["element"]
    return result


def canonical_condition(condition: dict[str, Any]) -> dict[str, Any]:
    result = {key: condition[key] for key in ("op", "left", "value", "text")}
    children = [canonical_condition(child) for child in condition.get("all", [])]
    result["all"] = sorted(children, key=canonical_json)
    result["predicate"] = None if condition["predicate"] is None else canonical_predicate_expression(condition["predicate"])
    return result


def canonical_state_mutation(mutation: dict[str, Any]) -> dict[str, Any]:
    result: dict[str, Any] = {
        "op": mutation["op"],
        "state": canonical_state_reference(mutation["state"]),
    }
    if mutation["numberValue"] is not None:
        result["numberValue"] = canonical_number_expression(mutation["numberValue"])
    if mutation["op"] == "set":
        result["boolValue"] = mutation["boolValue"]
        result["setValues"] = sorted(mutation["setValues"])
    elif mutation["op"] in ("setAdd", "setRemove"):
        result["element"] = mutation["element"]
    return result


def canonical_effect(effect: dict[str, Any]) -> dict[str, Any]:
    result = {key: effect[key] for key in ("type", "resource", "amount", "target", "key", "delay")}
    result["stateMutation"] = None if effect["stateMutation"] is None else canonical_state_mutation(effect["stateMutation"])
    return result


def canonical_dynamic_target_selector(selector: dict[str, Any]) -> dict[str, Any]:
    if selector["kind"] == "none":
        return dynamic_target_selector()
    return {key: selector[key] for key in ("kind", "ownership", "visibility", "minDistance", "maxDistance", "maxCandidates")}


def semantic_graph(rule_set: dict[str, Any]) -> dict[str, Any]:
    apply_turn = rule_set["applyTurn"]
    rules = [
        {
            "trigger": rule["trigger"],
            "condition": canonical_condition(rule["condition"]),
            "effects": [canonical_effect(effect) for effect in rule["effects"]],
            "stateDefinitions": sorted((canonical_state_definition(definition) for definition in rule["stateDefinitions"]), key=canonical_json),
            "priority": rule["priority"],
            "durationTurns": rule["durationTurns"],
            "appliedTurnOffset": rule["appliedTurn"] - apply_turn,
        }
        for rule in rule_set["changes"]
    ]
    actions = [
        {
            "spCost": action["spCost"],
            "resourceCost": action["resourceCost"],
            "resourceAmount": action["resourceAmount"],
            "cooldown": action["cooldown"],
            "availableTurnOffset": action["availableTurn"] - apply_turn,
            "targetSelector": canonical_dynamic_target_selector(action["targetSelector"]),
            "condition": canonical_condition(action["condition"]),
            "effects": [canonical_effect(effect) for effect in action["effects"]],
        }
        for action in rule_set["actions"]
    ]
    contracts = [
        {
            "progressKey": contract["progressKey"],
            "target": contract["target"],
            "minimumTurns": contract["minimumTurns"],
            "announcedTurnOffset": contract["announcedTurn"] - apply_turn,
            "achievableFromTurnOffset": contract["achievableFromTurn"] - apply_turn,
            "replaceWarningTurnOffset": 0 if contract["replaceWarningTurn"] == 0 else contract["replaceWarningTurn"] - apply_turn,
        }
        for contract in rule_set["victoryContracts"]
    ]
    return {
        "rules": sorted(rules, key=canonical_json),
        "actions": sorted(actions, key=canonical_json),
        "contracts": sorted(contracts, key=canonical_json),
    }


def graph_signature(rule_set: dict[str, Any]) -> str:
    return digest_json(semantic_graph(rule_set))


def signature_self_check() -> dict[str, bool]:
    score_reference = state_reference("run", "score")
    score_definition = {
        "scope": "run",
        "scopeId": "",
        "key": "score",
        "valueType": "number",
        "koreanName": "원정 점수",
        "iconToken": "score",
        "colorHex": "#33AAFF",
        "initialNumber": 1,
        "initialBool": False,
        "initialSet": [],
    }
    sample = {
        "schemaVersion": "v1",
        "requestId": "request-a",
        "applyTurn": 7,
        "koreanSummary": "발표 A",
        "changes": [
            {
                "id": "rule-a",
                "name": "표시 이름 A",
                "description": "표시 설명 A",
                "trigger": "turnStart",
                "condition": empty_condition(
                    predicate=predicate_expression(
                        "numberGreater",
                        left=number_expression("state", state=copy.deepcopy(score_reference)),
                        right=number_expression("constant", constant=0),
                    )
                ),
                "effects": [
                    {
                        "type": "typedState",
                        "resource": "none",
                        "amount": 0,
                        "target": "",
                        "key": "",
                        "value": "",
                        "delay": 0,
                        "stateMutation": state_mutation(
                            "add",
                            copy.deepcopy(score_reference),
                            number_value=number_expression("constant", constant=2),
                        ),
                    }
                ],
                "stateDefinitions": [score_definition],
                "priority": 0,
                "durationTurns": 3,
                "appliedTurn": 7,
                "worldCue": "연출 A",
            }
        ],
        "actions": [
            {
                "id": "action-a",
                "name": "표적 회유",
                "description": "보이는 비아군 유닛을 선택해 회유합니다.",
                "spCost": 2,
                "resourceCost": "none",
                "resourceAmount": 0,
                "cooldown": 2,
                "availableTurn": 7,
                "targetSelector": dynamic_target_selector("unit", ownership="nonPlayer", min_distance=1, max_distance=4),
                "condition": empty_condition(
                    predicate=predicate_expression(
                        "numberLessOrEqual",
                        left=number_expression("distance", selector="$actor", second_selector="$target"),
                        right=number_expression("constant", constant=4),
                    )
                ),
                "effects": [
                    {
                        "type": "factionSwitch",
                        "resource": "none",
                        "amount": 0,
                        "target": "$target",
                        "key": "1",
                        "value": "",
                        "delay": 0,
                        "stateMutation": None,
                    }
                ],
            }
        ],
        "victoryContracts": [],
    }
    presentation_variant = copy.deepcopy(sample)
    presentation_variant.update(requestId="request-b", koreanSummary="발표 B")
    presentation_variant["changes"][0].update(id="rule-b", name="표시 이름 B", description="표시 설명 B", worldCue="연출 B")
    presentation_variant["changes"][0]["stateDefinitions"][0].update(koreanName="새 원정 점수", iconToken="new_score", colorHex="#FFAA33")
    presentation_variant["actions"][0].update(id="action-b", name="표시 행동 B", description="표시 행동 설명 B")
    typed_semantic_variant = copy.deepcopy(sample)
    typed_semantic_variant["changes"][0]["effects"][0]["stateMutation"]["numberValue"]["constant"] = 3
    selector_semantic_variant = copy.deepcopy(sample)
    selector_semantic_variant["actions"][0]["targetSelector"]["maxDistance"] = 5
    noncanonical_none_selector = dynamic_target_selector(max_candidates=1)
    none_selector_errors: list[str] = []
    validate_dynamic_target_selector(noncanonical_none_selector, "noneSelector", none_selector_errors)
    none_selector_canonical = canonical_dynamic_target_selector(noncanonical_none_selector) == canonical_dynamic_target_selector(dynamic_target_selector())
    explored_unit_errors: list[str] = []
    validate_dynamic_target_selector(
        dynamic_target_selector("unit", visibility="explored"),
        "exploredUnitSelector",
        explored_unit_errors,
    )
    legacy_variant = copy.deepcopy(sample)
    del legacy_variant["changes"][0]["stateDefinitions"]
    legacy_action_variant = copy.deepcopy(sample)
    del legacy_action_variant["actions"][0]["targetSelector"]
    excessive_actions_variant = copy.deepcopy(sample)
    excessive_actions_variant["actions"] = []
    for action_index in range(MAX_RULESET_ACTIONS + 1):
        action = copy.deepcopy(sample["actions"][0])
        action["id"] = f"action-over-cap-{action_index}"
        excessive_actions_variant["actions"].append(action)

    current_errors = validate_rule_set_response(sample, "request-a", 7)
    legacy_errors = validate_rule_set_response(legacy_variant, "request-a", 7)
    legacy_action_errors = validate_rule_set_response(legacy_action_variant, "request-a", 7)
    excessive_action_errors = validate_rule_set_response(excessive_actions_variant, "request-a", 7)
    current_contract_accepted = not current_errors
    legacy_contract_rejected = bool(legacy_errors) and bool(legacy_action_errors)
    presentation_invariant = graph_signature(sample) == graph_signature(presentation_variant)
    typed_semantic_difference = graph_signature(sample) != graph_signature(typed_semantic_variant)
    selector_semantic_difference = graph_signature(sample) != graph_signature(selector_semantic_variant)
    excessive_actions_rejected = "actions:COUNT_INVALID" in excessive_action_errors
    if not current_contract_accepted or not legacy_contract_rejected or not presentation_invariant or not typed_semantic_difference or not selector_semantic_difference or not none_selector_errors or not none_selector_canonical or not explored_unit_errors or not excessive_actions_rejected:
        raise EvaluationError("rules-v4 contract or graph signature self-check failed")
    return {
        "currentStrictResponseAccepted": current_contract_accepted,
        "legacyContractRejected": legacy_contract_rejected,
        "presentationAndIdInvariant": presentation_invariant,
        "typedSemanticDifferenceDetected": typed_semantic_difference,
        "targetSelectorSemanticDifferenceDetected": selector_semantic_difference,
        "noneSelectorCanonicalizedAndStrict": bool(none_selector_errors) and none_selector_canonical,
        "entityExploredVisibilityRejected": bool(explored_unit_errors),
        "excessiveActionsRejected": excessive_actions_rejected,
    }


def normalize_api_origin(raw: str) -> str:
    parsed = urllib.parse.urlsplit(raw)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
        raise EvaluationError("--api-url must be an HTTPS origin without credentials")
    if parsed.path not in ("", "/") or parsed.query or parsed.fragment:
        raise EvaluationError("--api-url must not contain a path, query, or fragment")
    host = parsed.hostname
    if ":" in host and not host.startswith("["):
        host = "[" + host + "]"
    netloc = host + ((":" + str(parsed.port)) if parsed.port else "")
    return urllib.parse.urlunsplit(("https", netloc, "", "", ""))


def safe_error_code(status: int | None, body: Any, fallback: str) -> str:
    if status is None:
        return fallback
    server_code = body.get("error") if isinstance(body, dict) else None
    if isinstance(server_code, str) and 1 <= len(server_code) <= 64 and all(character.isupper() or character.isdigit() or character == "_" for character in server_code):
        return f"HTTP_{status}_{server_code}"
    return f"HTTP_{status}"


def classify_transport_error(error: BaseException) -> str:
    candidate: BaseException | object = error
    if isinstance(error, urllib.error.URLError):
        candidate = error.reason
    if isinstance(candidate, TimeoutError):
        return "CLIENT_TIMEOUT"
    if isinstance(candidate, ssl.SSLError):
        return "TLS_ERROR"
    return "NETWORK_ERROR"


def request_json(
    opener: urllib.request.OpenerDirector,
    url: str,
    method: str,
    payload: dict[str, Any] | None,
    headers: dict[str, str],
    timeout_seconds: float,
) -> dict[str, Any]:
    body = None if payload is None else canonical_json(payload).encode("utf-8")
    request_headers = {"Accept": "application/json", "User-Agent": "OnlyMyGame-Release-Evaluator/1.0", **headers}
    if body is not None:
        request_headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=body, headers=request_headers, method=method)
    started = time.monotonic()
    status: int | None = None
    response_headers: Any = None
    raw = b""
    transport_error: str | None = None
    try:
        try:
            with opener.open(request, timeout=timeout_seconds) as response:
                status = response.status
                response_headers = response.headers
                raw = response.read(MAX_RESPONSE_BYTES + 1)
        except urllib.error.HTTPError as error:
            status = error.code
            response_headers = error.headers
            try:
                raw = error.read(MAX_RESPONSE_BYTES + 1)
            finally:
                error.close()
    except (
        urllib.error.URLError,
        TimeoutError,
        ssl.SSLError,
        ConnectionResetError,
        http.client.IncompleteRead,
        http.client.RemoteDisconnected,
        http.client.HTTPException,
        OSError,
    ) as error:
        transport_error = classify_transport_error(error)
    elapsed_ms = round((time.monotonic() - started) * 1000.0, 3)
    selected_headers = {
        "generationAttempts": response_headers.get("X-OnlyMyGame-Generation-Attempts") if response_headers else None,
        "serverTiming": response_headers.get("Server-Timing") if response_headers else None,
        "retryAfter": response_headers.get("Retry-After") if response_headers else None,
    }
    if transport_error:
        return {"status": status, "body": None, "headers": selected_headers, "elapsedMs": elapsed_ms, "error": transport_error}
    if len(raw) > MAX_RESPONSE_BYTES:
        return {"status": status, "body": None, "headers": selected_headers, "elapsedMs": elapsed_ms, "error": "RESPONSE_TOO_LARGE"}
    parsed_body: Any = None
    parse_error = None
    try:
        parsed_body = json.loads(raw.decode("utf-8")) if raw else None
    except (UnicodeDecodeError, json.JSONDecodeError):
        parse_error = "INVALID_JSON_RESPONSE"
    error_code = parse_error
    if status is None or not 200 <= status < 300:
        error_code = safe_error_code(status, parsed_body, error_code or "NETWORK_ERROR")
    return {"status": status, "body": parsed_body, "headers": selected_headers, "elapsedMs": elapsed_ms, "error": error_code}


def rule_contract_headers(additional: dict[str, str] | None = None) -> dict[str, str]:
    headers = {COMPATIBILITY_HEADER: EXPECTED_COMPATIBILITY_VERSION}
    headers.update(additional or {})
    return headers


def transport_self_check() -> dict[str, str]:
    class FakeBody:
        def __init__(self, data: bytes = b"{}", read_error: BaseException | None = None, close_error: BaseException | None = None):
            self.data = data
            self.read_error = read_error
            self.close_error = close_error

        def read(self, amount: int = -1) -> bytes:
            if self.read_error is not None:
                raise self.read_error
            return self.data if amount < 0 else self.data[:amount]

        def close(self) -> None:
            if self.close_error is not None:
                raise self.close_error

    class FakeResponse(FakeBody):
        status = 200
        headers: dict[str, str] = {}

        def __enter__(self) -> "FakeResponse":
            return self

        def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> bool:
            self.close()
            return False

    class FakeOpener:
        def __init__(self, outcome: Any):
            self.outcome = outcome

        def open(self, request: Any, timeout: float) -> Any:
            if isinstance(self.outcome, BaseException):
                raise self.outcome
            return self.outcome

    def http_error(body: FakeBody) -> urllib.error.HTTPError:
        return urllib.error.HTTPError(
            "https://offline.invalid/v1/rules/generate",
            503,
            "Service Unavailable",
            {},
            body,
        )

    scenarios = {
        "successReadConnectionReset": (
            FakeOpener(FakeResponse(read_error=ConnectionResetError(54, "simulated reset"))),
            "NETWORK_ERROR",
            200,
        ),
        "successCloseTlsEof": (
            FakeOpener(FakeResponse(close_error=ssl.SSLEOFError(8, "simulated TLS EOF"))),
            "TLS_ERROR",
            200,
        ),
        "httpErrorReadIncomplete": (
            FakeOpener(http_error(FakeBody(read_error=http.client.IncompleteRead(b"{", 10)))),
            "NETWORK_ERROR",
            503,
        ),
        "httpErrorCloseOSError": (
            FakeOpener(http_error(FakeBody(close_error=OSError(5, "simulated close failure")))),
            "NETWORK_ERROR",
            503,
        ),
        "openTimeout": (
            FakeOpener(TimeoutError("simulated timeout")),
            "CLIENT_TIMEOUT",
            None,
        ),
        "openBadStatusLine": (
            FakeOpener(http.client.BadStatusLine("simulated malformed status")),
            "NETWORK_ERROR",
            None,
        ),
        "openLineTooLong": (
            FakeOpener(http.client.LineTooLong("simulated header")),
            "NETWORK_ERROR",
            None,
        ),
    }
    observed: dict[str, str] = {}
    for name, (opener, expected_error, expected_status) in scenarios.items():
        result = request_json(
            opener,
            "https://offline.invalid/v1/rules/generate",
            "POST",
            {"selfCheck": True},
            {},
            5.0,
        )
        if result["error"] != expected_error or result["status"] != expected_status or result["body"] is not None:
            raise EvaluationError("transport exception self-check failed: " + name)
        observed[name] = result["error"]
    return observed


def require_health(opener: urllib.request.OpenerDirector, api_origin: str, timeout_seconds: float) -> dict[str, Any]:
    result = request_json(opener, api_origin + "/health", "GET", None, {}, timeout_seconds)
    if result["status"] != 200 or not isinstance(result["body"], dict):
        raise EvaluationError("health preflight failed: " + (result["error"] or "INVALID_HEALTH_RESPONSE"))
    health = result["body"]
    limits = health.get("limits")
    if health.get("status") != "ok" or health.get("database") != "ok" or health.get("configured") is not True:
        raise EvaluationError("health preflight failed: service is not fully configured and healthy")
    if health.get("apiVersion") != EXPECTED_API_VERSION or health.get("compatibilityVersion") != EXPECTED_COMPATIBILITY_VERSION:
        raise EvaluationError("health preflight failed: API compatibility mismatch")
    if not isinstance(limits, dict):
        raise EvaluationError("health preflight failed: limits object is missing")
    per_client = limits.get("perClientDailyAttempts")
    global_limit = limits.get("globalDailyAttempts")
    if not is_int(per_client) or not is_int(global_limit) or per_client < CASE_COUNT or global_limit < CASE_COUNT:
        raise EvaluationError("health preflight failed: per-client and global daily limits must both be at least 100")
    return {
        "status": health.get("status"),
        "database": health.get("database"),
        "configured": True,
        "model": health.get("model") if isinstance(health.get("model"), str) else None,
        "apiVersion": health.get("apiVersion"),
        "compatibilityVersion": health.get("compatibilityVersion"),
        "limits": {
            "perClientDailyAttempts": per_client,
            "globalDailyAttempts": global_limit,
            "maxBurstAttemptsPerKey": limits.get("maxBurstAttemptsPerKey") if is_int(limits.get("maxBurstAttemptsPerKey")) else None,
        },
    }


def issue_session(opener: urllib.request.OpenerDirector, api_origin: str, run_id: str, timeout_seconds: float) -> str:
    result = request_json(
        opener,
        api_origin + "/v1/sessions",
        "POST",
        {"runId": run_id},
        rule_contract_headers(),
        timeout_seconds,
    )
    body = result["body"]
    token = body.get("token") if result["status"] == 200 and isinstance(body, dict) else None
    if not isinstance(token, str) or not token:
        raise EvaluationError("session preflight failed: " + (result["error"] or "SESSION_TOKEN_MISSING"))
    return token


def parse_generation_attempts(raw: Any) -> int | None:
    if not isinstance(raw, str) or raw not in ("1", "2"):
        return None
    return int(raw)


def parse_server_timing_ms(raw: Any) -> float | None:
    if not isinstance(raw, str):
        return None
    match = re.fullmatch(r"total;dur=([0-9]+(?:\.[0-9]{1,3})?)", raw.strip())
    if not match:
        return None
    value = float(match.group(1))
    return value if 0.0 <= value <= 60_000.0 else None


def nearest_rank_p95(values: list[float]) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(0.95 * len(ordered)) - 1)]


def default_output_path() -> Path:
    timestamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    return Path("artifacts") / "release-evaluation" / f"rules-evaluation-{timestamp}.json"


class ReportReservation:
    """Reserve one report target before networking and publish without overwrite."""

    def __init__(self, path: Path):
        # Normalize '.' and '..' without following symlinks so equivalent path
        # spellings share a reservation while a user-supplied symlink stays visible.
        self.output = Path(os.path.abspath(os.fspath(path.expanduser())))
        identity = hashlib.sha256(os.fsencode(str(self.output))).hexdigest()[:20]
        self.lock = self.output.parent / f".onlymygame-eval-{identity}.lock"
        self.lock_descriptor: int | None = None
        self.report_temporary: Path | None = None
        self.acquired = False
        self.published = False

    @staticmethod
    def _lexists(path: Path) -> bool:
        return os.path.lexists(path)

    @staticmethod
    def _write_descriptor(descriptor: int, data: bytes) -> None:
        offset = 0
        while offset < len(data):
            written = os.write(descriptor, data[offset:])
            if written <= 0:
                raise OSError("short local write")
            offset += written

    @staticmethod
    def _safe_unlink(path: Path | None) -> None:
        if path is None:
            return
        try:
            path.unlink(missing_ok=True)
        except OSError:
            pass

    def _probe_atomic_publication(self) -> None:
        suffix = secrets.token_hex(8)
        source = self.output.parent / f".onlymygame-eval-probe-{suffix}.tmp"
        destination = self.output.parent / f".onlymygame-eval-probe-{suffix}.link"
        descriptor: int | None = None
        try:
            descriptor = os.open(source, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            self._write_descriptor(descriptor, b"OnlyMyGame report publication probe\n")
            os.fsync(descriptor)
            os.close(descriptor)
            descriptor = None
            os.link(source, destination)
        finally:
            if descriptor is not None:
                try:
                    os.close(descriptor)
                except OSError:
                    pass
            self._safe_unlink(destination)
            self._safe_unlink(source)

    def acquire(self) -> None:
        if self.acquired:
            raise EvaluationError("report output reservation was already acquired")
        try:
            self.output.parent.mkdir(parents=True, exist_ok=True)
            if not self.output.parent.is_dir():
                raise EvaluationError("report output parent is not a directory")
            if self._lexists(self.output):
                raise EvaluationError("report output already exists; choose a new --output path")
            try:
                self.lock_descriptor = os.open(self.lock, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            except FileExistsError as error:
                raise EvaluationError("report output is reserved by another evaluation") from error
            self.acquired = True
            marker = f"OnlyMyGame report reservation pid={os.getpid()} created={utc_now_text()}\n".encode("ascii")
            self._write_descriptor(self.lock_descriptor, marker)
            os.fsync(self.lock_descriptor)
            if self._lexists(self.output):
                raise EvaluationError("report output appeared while acquiring its reservation")
            self._probe_atomic_publication()
        except EvaluationError:
            self.cleanup()
            raise
        except OSError as error:
            self.cleanup()
            raise EvaluationError("report output parent cannot safely create, write, or atomically publish a report") from error
        except BaseException:
            # KeyboardInterrupt/SystemExit must not strand a reservation either.
            self.cleanup()
            raise

    def publish(self, report: dict[str, Any]) -> None:
        if not self.acquired:
            raise EvaluationError("report output was not reserved before publication")
        if self.published:
            raise EvaluationError("report was already published")
        if self._lexists(self.output):
            raise EvaluationError("report output appeared during evaluation; it will not be overwritten")
        self.report_temporary = self.output.parent / (
            f".onlymygame-eval-report-{hashlib.sha256(os.fsencode(str(self.output))).hexdigest()[:12]}-{secrets.token_hex(8)}.tmp"
        )
        descriptor: int | None = None
        try:
            descriptor = os.open(self.report_temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                descriptor = None
                json.dump(report, stream, ensure_ascii=False, sort_keys=True, indent=2)
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            if self._lexists(self.output):
                raise EvaluationError("report output appeared during evaluation; it will not be overwritten")
            try:
                # A same-directory hard link atomically makes the complete file visible
                # and fails with EEXIST instead of overwriting a racing user file.
                os.link(self.report_temporary, self.output)
            except FileExistsError as error:
                raise EvaluationError("report output appeared during publication; it was not overwritten") from error
            self.published = True
            self._safe_unlink(self.report_temporary)
            self.report_temporary = None
        except EvaluationError:
            raise
        except OSError as error:
            raise EvaluationError("completed report could not be atomically published") from error
        finally:
            if descriptor is not None:
                try:
                    os.close(descriptor)
                except OSError:
                    pass

    def cleanup(self) -> None:
        self._safe_unlink(self.report_temporary)
        self.report_temporary = None
        same_lock = False
        if self.lock_descriptor is not None:
            try:
                held = os.fstat(self.lock_descriptor)
                visible = os.stat(self.lock, follow_symlinks=False)
                same_lock = held.st_dev == visible.st_dev and held.st_ino == visible.st_ino
            except OSError:
                same_lock = False
            try:
                os.close(self.lock_descriptor)
            except OSError:
                pass
            self.lock_descriptor = None
        if same_lock:
            self._safe_unlink(self.lock)
        self.acquired = False


def report_reservation_self_check() -> dict[str, bool]:
    with tempfile.TemporaryDirectory(prefix="onlymygame-report-reservation-") as directory:
        root = Path(directory)
        output = root / "nested" / "report.json"
        reservation = ReportReservation(output)
        reservation.acquire()
        parent_ready_before_publish = output.parent.is_dir() and reservation.lock.exists() and not output.exists()
        reservation.publish({"safe": True})
        reservation.cleanup()
        atomic_publish = json.loads(output.read_text(encoding="utf-8")) == {"safe": True} and not reservation.lock.exists()

        existing = root / "existing.json"
        existing.write_text("preserve-user-content", encoding="utf-8")
        existing_reservation = ReportReservation(existing)
        existing_refused = False
        try:
            existing_reservation.acquire()
        except EvaluationError:
            existing_refused = existing.read_text(encoding="utf-8") == "preserve-user-content"
        finally:
            existing_reservation.cleanup()

        interrupted = root / "interrupted.json"
        interrupted_reservation = ReportReservation(interrupted)
        interrupted_reservation.acquire()
        interrupted_lock = interrupted_reservation.lock
        interrupted_reservation.cleanup()
        interruption_cleanup = not interrupted.exists() and not interrupted_lock.exists()

        raced = root / "raced.json"
        raced_reservation = ReportReservation(raced)
        raced_reservation.acquire()
        raced.write_text("racing-user-content", encoding="utf-8")
        race_refused = False
        try:
            raced_reservation.publish({"mustNotOverwrite": True})
        except EvaluationError:
            race_refused = raced.read_text(encoding="utf-8") == "racing-user-content"
        finally:
            raced_reservation.cleanup()

        checks = {
            "parentReadyBeforeNetwork": parent_ready_before_publish,
            "atomicNoOverwritePublish": atomic_publish,
            "existingOutputRefused": existing_refused,
            "interruptionCleanup": interruption_cleanup,
            "racingOutputNotOverwritten": race_refused,
        }
        if not all(checks.values()):
            raise EvaluationError("report reservation self-check failed")
        return checks


def run_live(args: argparse.Namespace) -> int:
    if not args.api_url:
        raise EvaluationError("live evaluation requires an explicit --api-url")
    if os.environ.get(CONFIRMATION_ENV) != CONFIRMATION_VALUE:
        raise EvaluationError(f"network and paid evaluation refused: set {CONFIRMATION_ENV}={CONFIRMATION_VALUE}")
    api_origin = normalize_api_origin(args.api_url)
    requested_output = Path(args.output) if args.output else default_output_path()
    reservation = ReportReservation(requested_output)
    try:
        reservation.acquire()
        return run_reserved_evaluation(args, api_origin, reservation)
    finally:
        reservation.cleanup()


def run_reserved_evaluation(args: argparse.Namespace, api_origin: str, reservation: ReportReservation) -> int:
    opener = urllib.request.build_opener(NoRedirectHandler(), urllib.request.HTTPSHandler(context=ssl.create_default_context()))
    health = require_health(opener, api_origin, args.timeout)
    run_id = "release-eval-" + dt.datetime.now(dt.timezone.utc).strftime("%Y%m%d") + "-" + secrets.token_hex(8)
    session_token = issue_session(opener, api_origin, run_id, args.timeout)
    dataset = build_dataset(run_id)
    dataset_info = validate_dataset(dataset)

    cases: list[dict[str, Any]] = []
    valid_latencies_ms: list[float] = []
    signatures: set[str] = set()
    first_valid_count = 0
    repaired_valid_count = 0
    for index, snapshot in enumerate(dataset):
        request_id = "eval-" + hashlib.sha256(run_id.encode("utf-8")).hexdigest()[:16] + f"-{index:03d}"
        result = request_json(
            opener,
            api_origin + "/v1/rules/generate",
            "POST",
            snapshot,
            rule_contract_headers({
                "Authorization": "Bearer " + session_token,
                "Idempotency-Key": request_id,
                "X-Unity-Version": "release-evaluator-1.0",
            }),
            args.timeout,
        )
        attempts = parse_generation_attempts(result["headers"]["generationAttempts"])
        server_latency_ms = parse_server_timing_ms(result["headers"]["serverTiming"])
        validation_errors: list[str] = []
        if result["status"] == 200 and isinstance(result["body"], dict):
            validation_errors = validate_rule_set_response(result["body"], request_id, snapshot["turn"])
        elif result["status"] == 200:
            validation_errors = ["root:OBJECT_REQUIRED"]
        valid = result["status"] == 200 and attempts in (1, 2) and not validation_errors
        signature = graph_signature(result["body"]) if valid else None
        if valid:
            repaired_valid_count += 1
            valid_latencies_ms.append(result["elapsedMs"])
            signatures.add(signature)
            if attempts == 1:
                first_valid_count += 1
        error_category = None
        if not valid:
            if result["error"]:
                error_category = result["error"]
            elif attempts is None:
                error_category = "GENERATION_ATTEMPTS_HEADER_MISSING_OR_INVALID"
            elif validation_errors:
                error_category = "CLIENT_RULESET_VALIDATION_FAILED"
            else:
                error_category = "GENERATION_FAILED"
        cases.append(
            {
                "caseIndex": index,
                "seed": snapshot["seed"],
                "turn": snapshot["turn"],
                "httpStatus": result["status"],
                "generationAttempts": attempts,
                "firstResponseValid": valid and attempts == 1,
                "validIncludingRepair": valid,
                "clientLatencyMs": result["elapsedMs"],
                "serverLatencyMs": server_latency_ms,
                "graphSignatureSha256": signature,
                "errorCategory": error_category,
                "validationErrorCodes": validation_errors,
            }
        )
        if (index + 1) % 10 == 0:
            print(f"progress {index + 1:3d}/{CASE_COUNT}: first-valid={first_valid_count}, valid-with-repair={repaired_valid_count}", flush=True)

    first_rate = first_valid_count / CASE_COUNT
    repaired_rate = repaired_valid_count / CASE_COUNT
    p95_ms = nearest_rank_p95(valid_latencies_ms)
    unique_rate = len(signatures) / CASE_COUNT
    gates = {
        "firstResponseValidAtLeast95Percent": first_rate >= FIRST_VALID_THRESHOLD,
        "validIncludingRepairAtLeast99Percent": repaired_rate >= REPAIRED_VALID_THRESHOLD,
        "successfulGenerationClientP95AtMost8Seconds": p95_ms is not None and p95_ms <= P95_LATENCY_THRESHOLD_SECONDS * 1000.0,
        "uniqueSemanticGraphSignaturesAtLeast80Percent": unique_rate >= UNIQUE_SIGNATURE_THRESHOLD,
        "exactly100SequentialCasesCompleted": len(cases) == CASE_COUNT,
    }
    passed = all(gates.values())
    report = {
        "schemaVersion": "onlymygame-release-evaluation-v1",
        "generatedAtUtc": utc_now_text(),
        "evaluator": {"name": "scripts/evaluate-rules.py", "version": "1.0", "python": f"{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}"},
        "target": {"apiOrigin": api_origin, "health": health},
        "dataset": dataset_info,
        "thresholds": {
            "firstResponseValidRate": FIRST_VALID_THRESHOLD,
            "validIncludingRepairRate": REPAIRED_VALID_THRESHOLD,
            "successfulGenerationClientP95Seconds": P95_LATENCY_THRESHOLD_SECONDS,
            "uniqueSemanticGraphSignatureRate": UNIQUE_SIGNATURE_THRESHOLD,
        },
        "summary": {
            "caseCount": len(cases),
            "firstResponseValidCount": first_valid_count,
            "firstResponseValidRate": round(first_rate, 6),
            "validIncludingRepairCount": repaired_valid_count,
            "validIncludingRepairRate": round(repaired_rate, 6),
            "successfulGenerationClientP95Ms": p95_ms,
            "uniqueSemanticGraphSignatures": len(signatures),
            "uniqueSemanticGraphSignatureRate": round(unique_rate, 6),
            "passed": passed,
        },
        "gates": gates,
        "cases": cases,
        "dataHandling": {
            "rawPromptsStored": False,
            "rawSnapshotsStored": False,
            "rawResponsesStored": False,
            "sessionTokenStored": False,
            "runIdStored": False,
        },
    }
    reservation.publish(report)
    print(f"report: {reservation.output}")
    print(
        "result: " + ("PASS" if passed else "FAIL")
        + f"; first={first_rate:.1%}; with-repair={repaired_rate:.1%}; p95={'n/a' if p95_ms is None else f'{p95_ms / 1000.0:.3f}s'}; unique={unique_rate:.1%}"
    )
    return 0 if passed else 3


def run_dry() -> int:
    first = build_dataset("release-eval-dry-run")
    second = build_dataset("release-eval-dry-run")
    info = validate_dataset(first)
    if dataset_digest(first) != dataset_digest(second):
        raise EvaluationError("dataset determinism self-check failed")
    signature_checks = signature_self_check()
    transport_checks = transport_self_check()
    reservation_checks = report_reservation_self_check()
    contract_headers = rule_contract_headers({"X-Self-Check": "present"})
    if contract_headers != {
        COMPATIBILITY_HEADER: EXPECTED_COMPATIBILITY_VERSION,
        "X-Self-Check": "present",
    }:
        raise EvaluationError("request compatibility header self-check failed")
    print(
        canonical_json(
            {
                "dryRun": "PASS",
                "networkCalls": 0,
                "dataset": info,
                "graphSignature": signature_checks,
                "reportReservation": reservation_checks,
                "requestContract": {"compatibilityHeader": COMPATIBILITY_HEADER, "exactVersion": True},
                "transportErrors": transport_checks,
            }
        )
    )
    return 0


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Measure OnlyMyGame's 100-case commercial rule-generation release gates.")
    parser.add_argument("--dry-run", action="store_true", help="build and self-check the deterministic dataset and signature algorithm without any network access")
    parser.add_argument("--api-url", help="explicit HTTPS API origin, for example https://nas.example:10433 (live mode only)")
    parser.add_argument("--output", help="metrics JSON destination; defaults to artifacts/release-evaluation/rules-evaluation-<UTC>.json")
    parser.add_argument("--timeout", type=float, default=25.0, help="per-request client timeout in seconds (default: 25; live mode only)")
    args = parser.parse_args(argv)
    if args.timeout < 5.0 or args.timeout > 60.0:
        parser.error("--timeout must be between 5 and 60 seconds")
    if args.dry_run and (args.api_url or args.output):
        parser.error("--dry-run does not accept --api-url or --output because it performs no network call and writes no report")
    return args


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        return run_dry() if args.dry_run else run_live(args)
    except EvaluationError as error:
        print("evaluation refused or failed: " + str(error), file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("evaluation interrupted; no incomplete report was written", file=sys.stderr)
        return 130
    except Exception as error:
        # Do not print exception messages: urllib and JSON exceptions may contain a
        # URL or fragments of remote data. The type is enough for safe triage.
        print("evaluation failed with internal error type: " + type(error).__name__, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
