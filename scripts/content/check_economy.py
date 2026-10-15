"""Independent static checks of the generated economy; no runtime or price simulation."""
import argparse
from collections import Counter, defaultdict
import json
import math
from pathlib import Path
import sys


def local(reference):
    return reference.removeprefix("core:")


class CheckError(Exception):
    pass


class Economy:
    def __init__(self, root):
        self.root = root / "resources"
        self.paths = {}
        self.errors = []
        self.location = f"{self.root}: JSON"
        self.report = {"scope": "static content relationships and full-rate estimates; not a closed NPC economy"}
        self.data = defaultdict(dict)
        if not self.root.is_dir():
            raise CheckError(f"{self.root}: resources: directory does not exist")
        for path in sorted(self.root.rglob("*.json")):
            try:
                item = json.loads(path.read_text(encoding="utf-8"))
                kind = path.name.split(".")[-2]
                identity = item["id"]
                if identity in self.data[kind]:
                    raise CheckError(f"{path}: id: duplicate {identity}")
                self.data[kind][identity] = item
                self.paths[kind, identity] = path
            except (OSError, UnicodeError, ValueError, KeyError, TypeError) as error:
                raise CheckError(f"{path}: JSON/id: {error}") from error
        self.goods = {key: value["simulation"] for key, value in self.data["voxel"].items() if "simulation" in value}
        self.processes = {key: value["simulation"] for key, value in self.data["process"].items()}
        self.producers = defaultdict(list)
        for identity, process in self.processes.items():
            if process["kind"] == "production":
                for output in process["outputs"]:
                    self.producers[local(output["voxel"])].append(identity)

    def error(self, kind, identity, field, message):
        self.errors.append(f"{self.paths.get((kind, identity), self.root)}: {field}: {message}")

    def require(self, condition, kind, identity, field, message):
        if not condition:
            self.error(kind, identity, field, message)

    def cargo(self, entries):
        return {local(entry["voxel"]): entry["quantity"] for entry in entries}

    def cycles(self, kind):
        graph = defaultdict(set)
        for identity, process in self.processes.items():
            if process["kind"] == kind:
                for output in process["outputs"]:
                    graph[local(output["voxel"])].update(self.cargo(process.get("inputs", [])))
        visited, active = set(), set()

        def visit(good):
            if good in active:
                raise CheckError(f"cycle at {good}")
            if good in visited:
                return
            active.add(good)
            for dependency in sorted(graph[good]):
                visit(dependency)
            active.remove(good)
            visited.add(good)
        try:
            for good in list(graph):
                visit(good)
        except CheckError as error:
            self.error("process", next(identity for identity, p in self.processes.items() if p["kind"] == kind and good in self.cargo(p["outputs"])), "simulation.inputs", f"{kind} {error}")
        return not active

    def resources(self):
        profiles = self.data["generation-profile"]
        self.sources = set()
        self.source_terrains = defaultdict(set)
        resource_reports = {}
        for identity, profile in profiles.items():
            self.location = f"{self.paths['generation-profile', identity]}: configuration.resources"
            resources = profile["configuration"]["resources"]
            rows = []
            for index, resource in enumerate(resources):
                field = f"configuration.resources[{index}]"
                good = local(resource["good"])
                terrains = {local(t) for t in resource["allowed_terrain_types"]}
                valid = (good in self.goods and "raw_good" in self.goods[good].get("tags", []) and
                         bool(terrains) and all(t in self.data["terrain-type"] for t in terrains))
                self.require(valid, "generation-profile", identity, field + ".good", "requires a raw good and defined terrains")
                for key in ("probability", "global_regeneration_remaining_reserve_ratio"):
                    value = resource[key]
                    self.require(math.isfinite(value) and 0 <= value <= 1, "generation-profile", identity, field + "." + key, "must be finite in [0,1]")
                low, high = resource["minimum_reserve"], resource["maximum_reserve"]
                self.require(isinstance(low, int) and isinstance(high, int) and 0 <= low <= high <= 9223372036854775807, "generation-profile", identity, field, "reserves must be ordered nonnegative Int64 units")
                recovery, interval = resource["recovery_units_per_second"], resource["regeneration_interval_seconds"]
                self.require(math.isfinite(recovery) and recovery >= 0 and math.isfinite(interval) and interval > 0, "generation-profile", identity, field, "recovery uses nonnegative units/second; regeneration uses positive seconds")
                self.require(resource["renewable"] or recovery == 0, "generation-profile", identity, field + ".recovery_units_per_second", "nonrenewable resources require zero local recovery")
                if valid and resource["probability"] > 0 and high > 0:
                    self.sources.add(good)
                    self.source_terrains[good].update(terrains)
                rows.append({**resource, "recovery_units_per_hour": recovery * 3600, "regeneration_interval_hours": interval / 3600})
            resource_reports[identity] = rows
        self.report["resources"] = resource_reports
        self.report["resource_counts"] = {identity: len(rows) for identity, rows in resource_reports.items()}

    def relations(self):
        counts = Counter(p["kind"] for p in self.processes.values())
        self.report["catalog"] = {"goods": len(self.goods), **counts}
        self.report["extraction_terrain_semantics"] = (
            "each extraction process must share at least one terrain with a generated resource source; "
            "an absent process environment allows all defined terrains; resource terrains may extend beyond process requirements"
        )
        production_acyclic = self.cycles("production")
        recovery_acyclic = self.cycles("recovery")
        acyclic = production_acyclic and recovery_acyclic
        self.report["graphs"] = {"production_acyclic": production_acyclic, "recovery_acyclic": recovery_acyclic, "recovery_is_forward_source": False}
        extracted = set()
        values, primary = {}, []
        for good, simulation in self.goods.items():
            self.require(math.isfinite(simulation["value"]) and simulation["value"] >= 0, "voxel", good, "simulation.value", "requires finite nonnegative baseline value")
        for identity, p in self.processes.items():
            self.location = f"{self.paths['process', identity]}: simulation"
            for field in ("work_required", "power_per_work", "labor_per_work"):
                value = p[field]
                self.require(math.isfinite(value) and value >= (1 if field == "work_required" else 0), "process", identity, "simulation." + field, "invalid work/second, energy/work or labor/(work/second) parameter")
            self.require(isinstance(p["work_required"], int), "process", identity, "simulation.work_required", "must be whole work units")
            parameters = {field: p[field] for field in ("work_required", "power_per_work", "labor_per_work")}
            if p["kind"] == "extraction":
                good = local(p["extracts"]["voxel"])
                extracted.add(good)
                self.require(good in self.sources, "process", identity, "simulation.extracts", f"{good} has no generated node resource")
                environment = {local(t) for t in p.get("environment", {}).get("terrain_types", self.data["terrain-type"])}
                available = environment & self.source_terrains[good]
                self.require(bool(available), "process", identity, "simulation.environment.terrain_types", f"{good} has no generated resource on any allowed extraction terrain")
                values[identity] = {"kind": "extraction", "extracts": p["extracts"], **parameters,
                                    "process_terrain_types": sorted(environment),
                                    "resource_terrain_types": sorted(self.source_terrains[good]),
                                    "available_terrain_types": sorted(available)}
                continue
            inputs, outputs = self.cargo(p.get("inputs", [])), self.cargo(p["outputs"])
            for good, amount in {**inputs, **outputs}.items():
                self.require(good in self.goods and isinstance(amount, int) and 0 < amount <= 9223372036854775807, "process", identity, "simulation.inputs/outputs", f"invalid good or quantity: {good}")
            if not inputs:
                environment = {local(t) for t in p.get("environment", {}).get("terrain_types", [])}
                self.require(p["facility_tag"] == "agriculture" and bool(environment) and environment <= set(self.data["terrain-type"]), "process", identity, "simulation.inputs/environment", "primary production requires agriculture and defined terrain requirements")
                primary.append(identity)
            input_value = sum(self.goods[g]["value"] * q for g, q in inputs.items())
            output_value = sum(self.goods[g]["value"] * q for g, q in outputs.items())
            if inputs:
                self.require(output_value >= input_value if p["kind"] == "production" else output_value < input_value, "process", identity, "simulation.inputs/outputs", f"value invariant failed: input={input_value}, output={output_value}")
            values[identity] = {"kind": p["kind"], **parameters, "input_value": input_value, "output_value": output_value, "output_input_ratio": output_value / input_value if input_value else None}
        self.require(extracted == self.sources, "process", "", "simulation.extracts", "extraction goods must match generated node resources")
        reachable = extracted & self.sources
        while True:
            added = set()
            for p in self.processes.values():
                if p["kind"] == "production" and (p.get("inputs") or p["facility_tag"] == "agriculture" and p.get("environment", {}).get("terrain_types")) and set(self.cargo(p.get("inputs", []))) <= reachable:
                    added.update(self.cargo(p["outputs"]))
            if added <= reachable:
                break
            reachable.update(added)
        for identity, p in self.processes.items():
            if p["kind"] in ("production", "recovery"):
                self.require(set(self.cargo(p.get("inputs", []))) <= reachable, "process", identity, "simulation.inputs", "input lacks a legal forward source")
        consumption = set()
        for identity, rule in self.data["population-rule"].items():
            for entry in rule["simulation"]["consumption_goods"]:
                good = local(entry["good"])
                consumption.add(good)
                self.require(good in reachable, "population-rule", identity, "simulation.consumption_goods", f"{good} not reachable from node resources and primary production")
        self.report.update(value_checks=values, primary_production=primary, consumption_reachable=sorted(consumption & reachable), forward_reachable=sorted(reachable))
        if acyclic and not self.errors:
            self.report["raw_expansion"] = {identity: {route: self.expand(self.cargo(p.get("inputs", [])), route) for route in ("oil", "coal")} for identity, p in self.processes.items() if p["kind"] == "production"}

    def expand(self, inputs, route):
        selected = {}
        def producer(good):
            options = self.producers.get(good, [])
            if good == "fuel":
                return "oil-refining" if route == "oil" else "coal-fuel-production"
            return options[0] if options else None
        def collect(good):
            identity = producer(good)
            if identity is None or identity in selected:
                return
            p = self.processes[identity]
            selected[identity] = p
            for dependency in self.cargo(p.get("inputs", [])):
                collect(dependency)
        for good in inputs:
            collect(good)
        # Consumer processes precede suppliers so co-products share one joint batch demand.
        indegree = Counter({identity: 0 for identity in selected})
        edges = defaultdict(set)
        for identity, p in selected.items():
            for good in self.cargo(p.get("inputs", [])):
                supplier = producer(good)
                if supplier in selected and supplier not in edges[identity]:
                    edges[identity].add(supplier)
                    indegree[supplier] += 1
        queue = sorted(identity for identity in selected if not indegree[identity])
        demand, surplus, primary_units, batches = Counter(inputs), Counter(), Counter(), {}
        while queue:
            identity = queue.pop(0)
            p = selected[identity]
            outputs = self.cargo(p["outputs"])
            count = max((demand[g] + q - 1) // q for g, q in outputs.items())
            if count:
                batches[identity] = count
                if not p.get("inputs"):
                    primary_units.update({g: count * q for g, q in outputs.items()})
                for good, quantity in outputs.items():
                    surplus[good] += count * quantity - demand.pop(good, 0)
                for good, quantity in self.cargo(p.get("inputs", [])).items():
                    demand[good] += count * quantity
            for supplier in sorted(edges[identity]):
                indegree[supplier] -= 1
                if indegree[supplier] == 0:
                    queue.append(supplier)
        return {"node_resource_units": dict(sorted(demand.items())), "primary_production_units": dict(sorted(primary_units.items())), "whole_batches": batches, "surplus_units": {g: q for g, q in sorted(surplus.items()) if q}, "cost_allocation": "none; joint refinery batches, no single-product cost allocation"}

    def capability(self, identity, active=()):
        identity = local(identity)
        if identity in active:
            raise CheckError(f"{self.paths['blueprint', identity]}: layout.composites: recursive blueprint")
        self.location = f"{self.paths.get(('blueprint', identity), self.root)}: layout"
        blueprint = self.data["blueprint"][identity]
        totals = Counter()
        layout = blueprint["layout"]
        counts = Counter(cell[3] for cell in layout["cells"])
        for index, count in counts.items():
            voxel = local(layout["palette"][index]["voxel"])
            for name, parameters in self.goods.get(voxel, {}).get("behaviors", {}).items():
                if name in ("production", "extraction"):
                    totals[name] += count * parameters["work_rate"]
                elif name == "power":
                    totals["power"] += count * parameters["output"]
        for composite in layout.get("composites", []):
            totals.update(self.capability(composite["blueprint"], (*active, identity)))
        return totals

    def templates(self):
        rows, enabled_outputs = {}, set()
        for identity, template in self.data["settlement-template"].items():
            self.location = f"{self.paths['settlement-template', identity]}: production_plans"
            row = {"plans": [], "power_demand": 0, "labor_demand": 0, "rated_power_supply": 0}
            settlement_type = self.data["settlement-type"][local(template["settlement_type"])]["simulation"]
            terrains = {local(t) for t in settlement_type["preferred_terrain"]}
            buildings = {plot["id"]: plot["building"] for plot in template["plots"] if "building" in plot}
            capacities = {plot: self.capability(building) for plot, building in buildings.items()}
            row["rated_power_supply"] = sum(c["power"] for c in capacities.values())
            local_outputs, required, occupied = set(), set(), set()
            for index, plan in enumerate(template.get("production_plans", [])):
                if not plan["enabled"]:
                    continue
                self.location = f"{self.paths['settlement-template', identity]}: production_plans[{index}]"
                p = self.processes[local(plan["process"])]
                field = f"production_plans[{index}]"
                slot = (plan["plot"], plan["capability"])
                self.require(slot not in occupied, "settlement-template", identity, field, "multiple enabled plans share one building capability")
                occupied.add(slot)
                building = self.data["blueprint"][local(buildings[plan["plot"]])]
                self.require(p["facility_tag"] in building["simulation"].get("facility_tags", []), "settlement-template", identity, field + ".process", "building facility tags incompatible with process")
                rate = capacities.get(plan["plot"], Counter())[plan["capability"]]
                self.require(rate > 0, "settlement-template", identity, field + ".capability", "no matching capability in actual prebuilt blueprint")
                environment = {local(t) for t in p.get("environment", {}).get("terrain_types", [])}
                self.require(not environment or terrains <= environment, "settlement-template", identity, field + ".process", "process environment does not cover template terrains")
                if p["kind"] == "extraction":
                    good = local(p["extracts"]["voxel"])
                    allowed = set()
                    for profile in self.data["generation-profile"].values():
                        for resource in profile["configuration"]["resources"]:
                            if local(resource["good"]) == good and resource["probability"] > 0 and resource["maximum_reserve"] > 0:
                                allowed.update(local(t) for t in resource["allowed_terrain_types"])
                    self.require(terrains <= allowed, "settlement-template", identity, field + ".process", f"{good} lacks node resource source on template terrains")
                    outputs = self.cargo([p["extracts"]])
                else:
                    outputs = self.cargo(p["outputs"])
                    required.update(self.cargo(p.get("inputs", [])))
                power, labor = rate * p["power_per_work"], rate * p["labor_per_work"]
                row["power_demand"] += power
                row["labor_demand"] += labor
                row["plans"].append({
                    "plot": plan["plot"], "process": plan["process"], "work_per_second": rate,
                    "inputs_per_second": {g: q * rate / p["work_required"] for g, q in self.cargo(p.get("inputs", [])).items()},
                    "outputs_per_second": {g: q * rate / p["work_required"] for g, q in outputs.items()},
                    "power": power, "labor": labor,
                })
                if p["kind"] != "recovery":
                    local_outputs.update(outputs)
            enabled_outputs.update(local_outputs)
            row["import_dependencies"] = sorted(required - local_outputs)
            self.require(row["rated_power_supply"] >= row["power_demand"], "settlement-template", identity, "production_plans", f"rated power {row['rated_power_supply']} below full-rate estimate {row['power_demand']}")
            for rule in self.data["population-rule"].values():
                if "population_min" in settlement_type:
                    row["available_labor"] = {"min": settlement_type["population_min"] * rule["simulation"]["labor_ratio"], "max": settlement_type["population_max"] * rule["simulation"]["labor_ratio"]}
            rows[identity] = row
        for row in rows.values():
            row["imports_without_enabled_supplier"] = sorted(set(row["import_dependencies"]) - enabled_outputs)
        self.report["templates"] = rows
        npc = {}
        for identity, profile in self.data["generation-profile"].items():
            parameters = profile["configuration"]["npc_initialization"]
            deferred = (
                not parameters["fill_inputs_and_living_targets"]
                and parameters["output_reserve_hours"] == 0
                and parameters["minimum_for_sale_vehicles"] == 0
                and parameters["maximum_for_sale_vehicles"] == 0
                and not parameters["for_sale_vehicle_candidates"]
            )
            npc[identity] = {"status": "deferred" if deferred else "configured", "parameters": parameters}
        self.report["npc_initialization"] = npc

    def run(self):
        self.resources()
        self.relations()
        self.templates()
        self.report["passed"] = not self.errors
        self.report["errors"] = self.errors
        return self.report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("content_root", nargs="?", type=Path, default=Path(__file__).resolve().parents[2] / "builtin/core")
    args = parser.parse_args()
    economy = None
    try:
        economy = Economy(args.content_root)
        report = economy.run()
        output = json.dumps(report, ensure_ascii=True, indent=2, allow_nan=False)
    except (CheckError, KeyError, TypeError, ValueError, OSError, IndexError, RecursionError, ArithmeticError) as error:
        report = {"passed": False, "errors": (economy.errors if economy else []) + [f"{economy.location if economy else args.content_root}: JSON fields/relationships: {error}"]}
        output = json.dumps(report, ensure_ascii=True, indent=2)
    for error in report["errors"]:
        print(error, file=sys.stderr)
    print(output)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
