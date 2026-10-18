using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Sessions;

internal sealed record DecodedCommand(CoreInputId? InputIdentity, CoreCommand? Command);

internal static class CoreCommandEncoding
{
    internal static string Encode(CoreCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is CoreCommand.CommissionVehicleWork { Request: JobRequest.Refit { Instance: InstanceId.Building } or JobRequest.Dismantle })
            throw new ArgumentException("委托仅支持车辆建造、修理和改装。", nameof(command));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("input_format_version", 2);
            writer.WriteString("kind", command switch
            {
                CoreCommand.PublishContent => "content.publish",
                CoreCommand.ApplyInstanceDesign { Instance: InstanceId.Vehicle } => "instance.apply_design",
                CoreCommand.SetDeconstructionPaused => "construction.set_deconstruction_paused",
                CoreCommand.FormTrain => "train.form",
                CoreCommand.DisbandTrain => "train.disband",
                CoreCommand.ReplaceRunPlan => "train.replace_run_plan",
                CoreCommand.StartTrain => "train.start",
                CoreCommand.StopTrain => "train.stop",
                CoreCommand.DebugAddRailBreak => "rail.debug_add_break",
                CoreCommand.LoadGoods => "inventory.load_goods",
                CoreCommand.UnloadGoods => "inventory.unload_goods",
                CoreCommand.BuyGoods => "market.buy_goods",
                CoreCommand.SellGoods => "market.sell_goods",
                CoreCommand.BuyVehicle => "market.buy_vehicle",
                CoreCommand.SellVehicle => "market.sell_vehicle",
                CoreCommand.AcquireLease => "lease.acquire",
                CoreCommand.FoundSettlement => "settlement.found",
                CoreCommand.SetTrainEngagement => "engagement.set_train",
                CoreCommand.SetSettlementEngagement => "engagement.set_settlement",
                CoreCommand.Retreat => "combat.retreat",
                CoreCommand.FocusFire => "combat.focus_fire",
                CoreCommand.StartOwnedJob { Request: JobRequest.BuildVehicle or JobRequest.Repair { Instance: InstanceId.Vehicle } or JobRequest.Refit { Instance: InstanceId.Vehicle } or JobRequest.Dismantle { Instance: InstanceId.Vehicle } } => "construction.start_owned",
                CoreCommand.CommissionVehicleWork { Request: JobRequest.BuildVehicle or JobRequest.Repair { Instance: InstanceId.Vehicle } or JobRequest.Refit { Instance: InstanceId.Vehicle } } => "construction.commission_vehicle_work",
                _ => throw new NotImplementedException($"{command.GetType().Name} 尚未实现。")
            });
            switch (command)
            {
                case CoreCommand.FoundSettlement found:
                    WriteIdentity(writer, "node", found.Node.Value);
                    WriteIdentity(writer, "material_source", found.MaterialSource.Value);
                    break;
                case CoreCommand.SetTrainEngagement set:
                    WriteIdentity(writer, "train", set.Train.Value);
                    WriteTrainEngagement(writer, set.Rules);
                    break;
                case CoreCommand.SetSettlementEngagement set:
                    WriteIdentity(writer, "settlement", set.Settlement.Value);
                    WriteSettlementEngagement(writer, set.Rules);
                    break;
                case CoreCommand.Retreat retreat:
                    WriteIdentity(writer, "train", retreat.Train.Value);
                    break;
                case CoreCommand.FocusFire focus:
                    WriteIdentity(writer, "combat", focus.Combat.Value);
                    WriteController(writer, focus.Controller);
                    WriteWorldObject(writer, "target", focus.Target);
                    break;
                case CoreCommand.ApplyInstanceDesign apply:
                    WriteIdentity(writer, "vehicle", ((InstanceId.Vehicle)apply.Instance).Id.Value);
                    writer.WritePropertyName("change");
                    WorldJson.WriteDesignChange(writer, WorldInputs.NormalizeDesignChange(apply.Change));
                    break;
                case CoreCommand.SetDeconstructionPaused pause:
                    WriteIdentity(writer, "job", pause.Job.Value);
                    writer.WriteBoolean("paused", pause.Paused);
                    break;
                case CoreCommand.StartOwnedJob start:
                    WriteJobRequest(writer, start.Request);
                    break;
                case CoreCommand.CommissionVehicleWork commission:
                    WriteJobRequest(writer, commission.Request);
                    WriteNonnegativeInteger(writer, "maximum_payment", commission.MaximumPayment.Value);
                    break;
                case CoreCommand.BuyGoods buy:
                    WriteTransfer(writer, buy.Train, buy.Settlement, buy.LocalInventory, buy.Good, buy.Quantity);
                    WriteNonnegativeInteger(writer, "maximum_payment", buy.MaximumPayment.Value);
                    break;
                case CoreCommand.SellGoods sell:
                    WriteTransfer(writer, sell.Train, sell.Settlement, sell.LocalInventory, sell.Good, sell.Quantity);
                    WriteNonnegativeInteger(writer, "minimum_receipt", sell.MinimumReceipt.Value);
                    break;
                case CoreCommand.BuyVehicle buy:
                    WriteIdentity(writer, "seller", buy.Seller.Value);
                    WriteIdentity(writer, "vehicle", buy.Vehicle.Value);
                    WriteNonnegativeInteger(writer, "maximum_payment", buy.MaximumPayment.Value);
                    break;
                case CoreCommand.SellVehicle sell:
                    WriteIdentity(writer, "buyer", sell.Buyer.Value);
                    WriteIdentity(writer, "vehicle", sell.Vehicle.Value);
                    WriteNonnegativeInteger(writer, "minimum_receipt", sell.MinimumReceipt.Value);
                    break;
                case CoreCommand.LoadGoods load:
                    WriteTransfer(writer, load.Train, load.Settlement, load.LocalInventory, load.Good, load.Quantity);
                    break;
                case CoreCommand.UnloadGoods unload:
                    WriteTransfer(writer, unload.Train, unload.Settlement, unload.LocalInventory, unload.Good, unload.Quantity);
                    break;
                case CoreCommand.AcquireLease acquire:
                    if (acquire.MaximumPayment.Value < 0)
                        throw new ArgumentException("付款上限必须非负。", nameof(command));
                    writer.WriteStartObject("request");
                    switch (acquire.Request)
                    {
                        case LeaseRequest.Rent rent:
                            writer.WriteString("kind", "rent");
                            WriteIdentity(writer, "settlement", rent.Settlement.Value);
                            WritePositive(writer, "capacity", rent.Capacity);
                            WriteIdentity(writer, "duration_steps", rent.DurationSteps);
                            break;
                        case LeaseRequest.Renew renew:
                            writer.WriteString("kind", "renew");
                            WriteIdentity(writer, "lease", renew.Lease.Value);
                            WriteIdentity(writer, "duration_steps", renew.DurationSteps);
                            break;
                        default: throw new ArgumentException("租约请求无效。", nameof(command));
                    }
                    writer.WriteEndObject();
                    writer.WriteNumber("maximum_payment", acquire.MaximumPayment.Value);
                    break;
                case CoreCommand.PublishContent publish:
                    if (!ValidIdentity(publish.Content.Identity.Value))
                        throw new ArgumentException("输入身份必须是大写 SHA-256。", nameof(command));
                    writer.WriteString("input_identity", publish.Content.Identity.Value);
                    break;
                case CoreCommand.FormTrain form:
                    if (form.OrderedVehicles.IsDefault) throw new ArgumentException("数组必须初始化。", nameof(command));
                    writer.WriteStartArray("ordered_vehicles");
                    foreach (var vehicle in form.OrderedVehicles)
                    {
                        if (vehicle.Value == 0) throw new ArgumentException("世界身份必须非零。", nameof(command));
                        writer.WriteNumberValue(vehicle.Value);
                    }
                    writer.WriteEndArray();
                    break;
                case CoreCommand.ReplaceRunPlan replace:
                    WriteIdentity(writer, "train", replace.Train.Value);
                    ArgumentNullException.ThrowIfNull(replace.Plan);
                    if (replace.Plan.Stops.IsDefault) throw new ArgumentException("数组必须初始化。", nameof(command));
                    writer.WriteStartObject("plan");
                    writer.WriteBoolean("loop", replace.Plan.Loop);
                    writer.WriteStartArray("stops");
                    foreach (var stop in replace.Plan.Stops)
                    {
                        writer.WriteStartObject();
                        switch (stop)
                        {
                            case PlanStop.Waypoint waypoint:
                                writer.WriteString("kind", "waypoint");
                                WriteIdentity(writer, "node", waypoint.Node.Value);
                                break;
                            case PlanStop.Station station:
                                if (station.GoodsRules.IsDefault) throw new ArgumentException("数组必须初始化。", nameof(command));
                                writer.WriteString("kind", "station");
                                WriteIdentity(writer, "node", station.Node.Value);
                                writer.WriteNumber("dwell_steps", station.DwellSteps);
                                writer.WriteStartArray("goods_rules");
                                foreach (var rule in station.GoodsRules) WriteGoodsRule(writer, rule);
                                writer.WriteEndArray();
                                break;
                            default: throw new ArgumentException("计划站点无效。", nameof(command));
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    break;
                case CoreCommand.DisbandTrain disband: WriteIdentity(writer, "train", disband.Train.Value); break;
                case CoreCommand.StartTrain start: WriteIdentity(writer, "train", start.Train.Value); break;
                case CoreCommand.StopTrain stop: WriteIdentity(writer, "train", stop.Train.Value); break;
                case CoreCommand.DebugAddRailBreak addBreak:
                    WriteIdentity(writer, "edge", addBreak.Edge.Value);
                    if (addBreak.Track < 1) throw new ArgumentException("铁轨号必须为正。", nameof(command));
                    if (!double.IsFinite(addBreak.DistanceMeters) || addBreak.DistanceMeters < 0)
                        throw new ArgumentException("断点距离必须是非负有限值。", nameof(command));
                    writer.WriteNumber("track", addBreak.Track);
                    writer.WritePropertyName("distance_meters");
                    writer.WriteRawValue(addBreak.DistanceMeters.ToString("R", CultureInfo.InvariantCulture));
                    break;
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static CoreResult<DecodedCommand> Decode(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("input_format_version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetUInt32(out var number) || number != 2)
                return Failure("unsupported_format", "/input_format_version");
            if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                return Failure("unsupported_format", "/kind");
            var name = Text(kind, "/kind");
            string[]? fields = name switch
            {
                "instance.apply_design" => ["input_format_version", "kind", "vehicle", "change"],
                "construction.set_deconstruction_paused" => ["input_format_version", "kind", "job", "paused"],
                "content.publish" => new[] { "input_format_version", "kind", "input_identity" },
                "train.form" => ["input_format_version", "kind", "ordered_vehicles"],
                "train.replace_run_plan" => ["input_format_version", "kind", "train", "plan"],
                "train.disband" or "train.start" or "train.stop" => ["input_format_version", "kind", "train"],
                "rail.debug_add_break" => ["input_format_version", "kind", "edge", "track", "distance_meters"],
                "inventory.load_goods" or "inventory.unload_goods" => ["input_format_version", "kind", "train", "settlement", "local_inventory", "good", "quantity"],
                "lease.acquire" => ["input_format_version", "kind", "request", "maximum_payment"],
                "settlement.found" => ["input_format_version", "kind", "node", "material_source"],
                "engagement.set_train" => ["input_format_version", "kind", "train", "rules"],
                "engagement.set_settlement" => ["input_format_version", "kind", "settlement", "rules"],
                "combat.retreat" => ["input_format_version", "kind", "train"],
                "combat.focus_fire" => ["input_format_version", "kind", "combat", "controller", "target"],
                "construction.start_owned" => ["input_format_version", "kind", "request"],
                "construction.commission_vehicle_work" => ["input_format_version", "kind", "request", "maximum_payment"],
                "market.buy_goods" => ["input_format_version", "kind", "train", "settlement", "local_inventory", "good", "quantity", "maximum_payment"],
                "market.sell_goods" => ["input_format_version", "kind", "train", "settlement", "local_inventory", "good", "quantity", "minimum_receipt"],
                "market.buy_vehicle" => ["input_format_version", "kind", "seller", "vehicle", "maximum_payment"],
                "market.sell_vehicle" => ["input_format_version", "kind", "buyer", "vehicle", "minimum_receipt"],
                _ => null
            };
            if (fields is null) return Failure("unsupported_format", "/kind");
            Fields(root, "", fields);
            if (name == "settlement.found")
                return Success(new(null, new CoreCommand.FoundSettlement(
                    new(Identity(Property(root, "node", "", JsonValueKind.Number), "/node")),
                    new(Identity(Property(root, "material_source", "", JsonValueKind.Number), "/material_source")))));
            if (name == "engagement.set_train")
                return Success(new(null, new CoreCommand.SetTrainEngagement(
                    new(Identity(Property(root, "train", "", JsonValueKind.Number), "/train")),
                    ReadTrainEngagement(Property(root, "rules", "", JsonValueKind.Object)))));
            if (name == "engagement.set_settlement")
                return Success(new(null, new CoreCommand.SetSettlementEngagement(
                    new(Identity(Property(root, "settlement", "", JsonValueKind.Number), "/settlement")),
                    ReadSettlementEngagement(Property(root, "rules", "", JsonValueKind.Object)))));
            if (name == "combat.retreat")
                return Success(new(null, new CoreCommand.Retreat(
                    new(Identity(Property(root, "train", "", JsonValueKind.Number), "/train")))));
            if (name == "combat.focus_fire")
                return Success(new(null, new CoreCommand.FocusFire(
                    new(Identity(Property(root, "combat", "", JsonValueKind.Number), "/combat")),
                    ReadController(Property(root, "controller", "", JsonValueKind.Object)),
                    ReadWorldObject(Property(root, "target", "", JsonValueKind.Object), "/target"))));
            if (name == "instance.apply_design")
                return Success(new(null, new CoreCommand.ApplyInstanceDesign(
                    new InstanceId.Vehicle(new(Identity(Property(root, "vehicle", "", JsonValueKind.Number), "/vehicle"))), ReadDesignChange(root, ""))));
            if (name == "construction.set_deconstruction_paused")
            {
                if (!root.TryGetProperty("paused", out var paused) || paused.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidField("/paused");
                return Success(new(null, new CoreCommand.SetDeconstructionPaused(
                    new(Identity(Property(root, "job", "", JsonValueKind.Number), "/job")), paused.GetBoolean())));
            }
            if (name == "construction.start_owned")
                return Success(new(null, new CoreCommand.StartOwnedJob(ReadJobRequest(Property(root, "request", "", JsonValueKind.Object)))));
            if (name == "construction.commission_vehicle_work")
                return Success(new(null, new CoreCommand.CommissionVehicleWork(
                    ReadJobRequest(Property(root, "request", "", JsonValueKind.Object), true), new(ReadNonnegativeInteger(root, "maximum_payment")))));
            if (name == "lease.acquire")
            {
                var request = Property(root, "request", "", JsonValueKind.Object);
                var requestKind = Text(Property(request, "kind", "/request", JsonValueKind.String), "/request/kind");
                if (requestKind is not ("rent" or "renew")) throw new InvalidField("/request/kind");
                Fields(request, "/request", requestKind == "rent"
                    ? ["kind", "settlement", "capacity", "duration_steps"] : ["kind", "lease", "duration_steps"]);
                var duration = Identity(Property(request, "duration_steps", "/request", JsonValueKind.Number), "/request/duration_steps");
                LeaseRequest leaseRequest = requestKind == "rent"
                    ? new LeaseRequest.Rent(new(Identity(Property(request, "settlement", "/request", JsonValueKind.Number), "/request/settlement")),
                        Positive(Property(request, "capacity", "/request", JsonValueKind.Number), "/request/capacity"), duration)
                    : new LeaseRequest.Renew(new(Identity(Property(request, "lease", "/request", JsonValueKind.Number), "/request/lease")), duration);
                var payment = Property(root, "maximum_payment", "", JsonValueKind.Number);
                if (!payment.TryGetInt64(out var money) || money < 0) throw new InvalidField("/maximum_payment");
                return Success(new(null, new CoreCommand.AcquireLease(leaseRequest, new(money))));
            }
            if (name is "market.buy_vehicle" or "market.sell_vehicle")
            {
                var partyName = name == "market.buy_vehicle" ? "seller" : "buyer";
                var party = new SettlementId(Identity(Property(root, partyName, "", JsonValueKind.Number), "/" + partyName));
                var vehicle = new VehicleId(Identity(Property(root, "vehicle", "", JsonValueKind.Number), "/vehicle"));
                var moneyName = name == "market.buy_vehicle" ? "maximum_payment" : "minimum_receipt";
                var money = new Money(ReadNonnegativeInteger(root, moneyName));
                return Success(new(null, name == "market.buy_vehicle"
                    ? new CoreCommand.BuyVehicle(party, vehicle, money)
                    : new CoreCommand.SellVehicle(party, vehicle, money)));
            }
            if (name is "inventory.load_goods" or "inventory.unload_goods" or "market.buy_goods" or "market.sell_goods")
            {
                var transferTrain = new TrainId(Identity(Property(root, "train", "", JsonValueKind.Number), "/train"));
                var settlement = new SettlementId(Identity(Property(root, "settlement", "", JsonValueKind.Number), "/settlement"));
                var inventory = new InventoryId(Identity(Property(root, "local_inventory", "", JsonValueKind.Number), "/local_inventory"));
                var good = ReadGood(Property(root, "good", "", JsonValueKind.Object));
                var quantity = Positive(Property(root, "quantity", "", JsonValueKind.Number), "/quantity");
                CoreCommand transfer = name switch
                {
                    "inventory.load_goods" => new CoreCommand.LoadGoods(transferTrain, settlement, inventory, good, quantity),
                    "inventory.unload_goods" => new CoreCommand.UnloadGoods(transferTrain, settlement, inventory, good, quantity),
                    "market.buy_goods" => new CoreCommand.BuyGoods(transferTrain, settlement, inventory, good, quantity, new(ReadNonnegativeInteger(root, "maximum_payment"))),
                    _ => new CoreCommand.SellGoods(transferTrain, settlement, inventory, good, quantity, new(ReadNonnegativeInteger(root, "minimum_receipt")))
                };
                return Success(new(null, transfer));
            }
            if (name == "content.publish")
            {
                var identity = Text(Property(root, "input_identity", "", JsonValueKind.String), "/input_identity");
                if (!ValidIdentity(identity)) throw new InvalidField("/input_identity");
                return Success(new(new(identity!), null));
            }
            if (name == "train.form")
            {
                var vehicles = Property(root, "ordered_vehicles", "", JsonValueKind.Array);
                var builder = ImmutableArray.CreateBuilder<VehicleId>();
                foreach (var vehicle in vehicles.EnumerateArray())
                    builder.Add(new(Identity(vehicle, $"/ordered_vehicles/{builder.Count}")));
                return Success(new(null, new CoreCommand.FormTrain(builder.ToImmutable())));
            }
            if (name == "rail.debug_add_break")
            {
                var edge = new EdgeId(Identity(Property(root, "edge", "", JsonValueKind.Number), "/edge"));
                var track = Property(root, "track", "", JsonValueKind.Number);
                if (!track.TryGetInt32(out var trackNumber) || trackNumber < 1) throw new InvalidField("/track");
                var distance = Property(root, "distance_meters", "", JsonValueKind.Number);
                if (!distance.TryGetDouble(out var meters) || !double.IsFinite(meters) || meters < 0)
                    throw new InvalidField("/distance_meters");
                return Success(new(null, new CoreCommand.DebugAddRailBreak(edge, trackNumber, meters)));
            }
            var train = new TrainId(Identity(Property(root, "train", "", JsonValueKind.Number), "/train"));
            CoreCommand command = name switch
            {
                "train.disband" => new CoreCommand.DisbandTrain(train),
                "train.start" => new CoreCommand.StartTrain(train),
                "train.stop" => new CoreCommand.StopTrain(train),
                _ => new CoreCommand.ReplaceRunPlan(train, Plan(Property(root, "plan", "", JsonValueKind.Object)))
            };
            return Success(new(null, command));
        }
        catch (JsonException) { return Failure("unsupported_format", ""); }
        catch (InvalidField error) { return Failure("invalid_state", error.Field); }
        catch (InvalidOperationException) { return Failure("invalid_state", ""); }
    }

    private static void WriteJobRequest(Utf8JsonWriter writer, JobRequest request)
    {
        var (facility, sources, priority) = request switch
        {
            JobRequest.BuildVehicle construction => (construction.Facility, construction.Sources, construction.Priority),
            JobRequest.Repair { Instance: InstanceId.Vehicle } repair => (repair.Facility, repair.Sources, repair.Priority),
            JobRequest.Refit { Instance: InstanceId.Vehicle } refit => (refit.Facility, refit.Sources, refit.Priority),
            JobRequest.Dismantle { Instance: InstanceId.Vehicle } dismantle => (dismantle.Facility, ImmutableArray<InventoryId>.Empty, dismantle.Priority),
            _ => throw new NotImplementedException($"{request?.GetType().Name} 尚未实现。")
        };
        ArgumentNullException.ThrowIfNull(facility);
        if (!Enum.IsDefined(facility.Capability)) throw new ArgumentException("能力种类无效。", nameof(request));
        if (sources.IsDefault) throw new ArgumentException("数组必须初始化。", nameof(request));
        writer.WriteStartObject("request");
        writer.WriteString("kind", request switch { JobRequest.BuildVehicle => "build_vehicle", JobRequest.Refit => "refit_vehicle", JobRequest.Dismantle => "dismantle_vehicle", _ => "repair_vehicle" });
        writer.WriteStartObject("facility");
        WriteIdentity(writer, "building", facility.Building.Value);
        writer.WriteString("capability", facility.Capability.ToString().ToLowerInvariant());
        writer.WriteEndObject();
        if (request is JobRequest.BuildVehicle build)
        {
            var input = WorldInputs.NormalizeDesignInput(build.Design);
            writer.WritePropertyName("design");
            WorldJson.WriteDesignInput(writer, input);
        }
        else
        {
            var instance = request switch { JobRequest.Repair repair => repair.Instance, JobRequest.Refit refit => refit.Instance, JobRequest.Dismantle dismantle => dismantle.Instance, _ => throw new ArgumentException("施工请求无效。", nameof(request)) };
            WriteIdentity(writer, "vehicle", ((InstanceId.Vehicle)instance).Id.Value);
            if (request is JobRequest.Refit refitRequest)
            {
                writer.WritePropertyName("change");
                WorldJson.WriteDesignChange(writer, WorldInputs.NormalizeDesignChange(refitRequest.Change));
            }
        }
        if (request is not JobRequest.Dismantle)
        {
            writer.WriteStartArray("sources");
            foreach (var source in sources)
            {
                if (source.Value == 0) throw new ArgumentException("世界身份必须非零。", nameof(request));
                writer.WriteNumberValue(source.Value);
            }
            writer.WriteEndArray();
        }
        writer.WriteNumber("priority", priority);
        writer.WriteEndObject();
    }

    private static JobRequest ReadJobRequest(JsonElement request, bool commission = false)
    {
        const string field = "/request";
        var kind = Text(Property(request, "kind", field, JsonValueKind.String), field + "/kind");
        if (kind is not ("build_vehicle" or "repair_vehicle" or "refit_vehicle" or "dismantle_vehicle") || commission && kind == "dismantle_vehicle") throw new InvalidField(field + "/kind");
        Fields(request, field, kind switch
        {
            "build_vehicle" => ["kind", "facility", "design", "sources", "priority"],
            "refit_vehicle" => ["kind", "facility", "vehicle", "change", "sources", "priority"],
            "dismantle_vehicle" => ["kind", "facility", "vehicle", "priority"],
            _ => ["kind", "facility", "vehicle", "sources", "priority"]
        });
        var facilityValue = Property(request, "facility", field, JsonValueKind.Object);
        Fields(facilityValue, field + "/facility", ["building", "capability"]);
        var capability = Text(Property(facilityValue, "capability", field + "/facility", JsonValueKind.String), field + "/facility/capability");
        var capabilityKind = capability switch
        {
            "production" => CapabilityKind.Production,
            "extraction" => CapabilityKind.Extraction,
            "construction" => CapabilityKind.Construction,
            _ => throw new InvalidField(field + "/facility/capability")
        };
        var facility = new FacilityKey(new(Identity(Property(facilityValue, "building", field + "/facility", JsonValueKind.Number), field + "/facility/building")), capabilityKind);
        var sources = ImmutableArray.CreateBuilder<InventoryId>();
        if (kind != "dismantle_vehicle")
            foreach (var source in Property(request, "sources", field, JsonValueKind.Array).EnumerateArray())
                sources.Add(new(Identity(source, $"{field}/sources/{sources.Count}")));
        var priority = Property(request, "priority", field, JsonValueKind.Number);
        if (!priority.TryGetInt32(out var number)) throw new InvalidField(field + "/priority");
        if (kind == "dismantle_vehicle")
            return new JobRequest.Dismantle(facility, new InstanceId.Vehicle(new(Identity(Property(request, "vehicle", field, JsonValueKind.Number), field + "/vehicle"))), number);
        if (kind == "refit_vehicle")
            return new JobRequest.Refit(facility, new InstanceId.Vehicle(new(Identity(Property(request, "vehicle", field, JsonValueKind.Number), field + "/vehicle"))), ReadDesignChange(request, field), sources.ToImmutable(), number);
        if (kind == "repair_vehicle")
            return new JobRequest.Repair(facility, new InstanceId.Vehicle(new(Identity(Property(request, "vehicle", field, JsonValueKind.Number), field + "/vehicle"))), sources.ToImmutable(), number);
        try
        {
            var input = WorldInputs.NormalizeDesignInput(WorldJson.ReadDesignInput(Property(request, "design", field, JsonValueKind.Object)));
            return new JobRequest.BuildVehicle(facility, input, sources.ToImmutable(), number);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidField(field + "/design"); }
    }

    private static DesignChangeProjection ReadDesignChange(JsonElement value, string field)
    {
        try
        {
            return WorldInputs.NormalizeDesignChange(WorldJson.ReadDesignChange(Property(value, "change", field, JsonValueKind.Object)));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidField(field + "/change"); }
    }

    private static void WriteTransfer(Utf8JsonWriter writer, TrainId train, SettlementId settlement,
        InventoryId inventory, GoodId good, long quantity)
    {
        WriteIdentity(writer, "train", train.Value);
        WriteIdentity(writer, "settlement", settlement.Value);
        WriteIdentity(writer, "local_inventory", inventory.Value);
        WriteGood(writer, good);
        WritePositive(writer, "quantity", quantity);
    }

    private static void WriteGood(Utf8JsonWriter writer, GoodId good)
    {
        WorldInputs.ValidateGood(good);
        writer.WriteStartObject("good");
        writer.WriteString("kind", good switch { GoodId.Voxel => "voxel", GoodId.Composite => "composite", GoodId.Assembly => "assembly", _ => throw new ArgumentException("货物无效。", nameof(good)) });
        switch (good)
        {
            case GoodId.Voxel voxel: writer.WriteString("definition", voxel.Definition.Value); break;
            case GoodId.Composite composite: writer.WriteString("definition", composite.Definition.Value); break;
            case GoodId.Assembly assembly: writer.WriteString("composition", assembly.Composition.Value); break;
        }
        writer.WriteEndObject();
    }

    private static GoodId ReadGood(JsonElement value, string field = "/good")
    {
        var kind = Text(Property(value, "kind", field, JsonValueKind.String), field + "/kind");
        if (kind is not ("voxel" or "composite" or "assembly")) throw new InvalidField(field + "/kind");
        var name = kind == "assembly" ? "composition" : "definition";
        Fields(value, field, ["kind", name]);
        var identity = Text(Property(value, name, field, JsonValueKind.String), field + "/" + name);
        GoodId good = kind switch
        {
            "voxel" => new GoodId.Voxel(new(identity!)),
            "composite" => new GoodId.Composite(new(identity!)),
            _ => new GoodId.Assembly(new(identity!))
        };
        try { WorldInputs.ValidateGood(good); }
        catch (ArgumentException) { throw new InvalidField(field + "/" + name); }
        return good;
    }

    private static void WriteTrainEngagement(Utf8JsonWriter writer, TrainEngagementRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!CombatRules.Valid(rules)) throw new ArgumentException("列车交战规则无效。", nameof(rules));
        writer.WriteStartObject("rules");
        WritePriority(writer, rules.Priority);
        WriteScope(writer, rules.Scope);
        writer.WriteString("encounter", rules.Encounter switch
        {
            EncounterAction.Engage => "engage",
            EncounterAction.Retreat => "retreat",
            _ => throw new ArgumentException("遇敌行为无效。", nameof(rules))
        });
        writer.WritePropertyName("retreat_structure_ratio");
        writer.WriteRawValue(rules.RetreatStructureRatio.ToString("R", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static TrainEngagementRules ReadTrainEngagement(JsonElement value)
    {
        const string field = "/rules";
        Fields(value, field, ["priority", "scope", "encounter", "retreat_structure_ratio"]);
        var priority = ReadPriority(Property(value, "priority", field, JsonValueKind.String), field + "/priority");
        var scope = ReadScope(Property(value, "scope", field, JsonValueKind.String), field + "/scope");
        var encounter = Text(Property(value, "encounter", field, JsonValueKind.String), field + "/encounter") switch
        {
            "engage" => EncounterAction.Engage,
            "retreat" => EncounterAction.Retreat,
            _ => throw new InvalidField(field + "/encounter")
        };
        var ratio = Double(Property(value, "retreat_structure_ratio", field, JsonValueKind.Number), field + "/retreat_structure_ratio");
        var rules = new TrainEngagementRules(priority, scope, encounter, ratio);
        if (!CombatRules.Valid(rules)) throw new InvalidField(field + "/retreat_structure_ratio");
        return rules;
    }

    private static void WriteSettlementEngagement(Utf8JsonWriter writer, SettlementEngagementRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!CombatRules.Valid(rules)) throw new ArgumentException("聚居地交战规则无效。", nameof(rules));
        writer.WriteStartObject("rules");
        WritePriority(writer, rules.Priority);
        WriteScope(writer, rules.Scope);
        writer.WriteEndObject();
    }

    private static SettlementEngagementRules ReadSettlementEngagement(JsonElement value)
    {
        const string field = "/rules";
        Fields(value, field, ["priority", "scope"]);
        return new(ReadPriority(Property(value, "priority", field, JsonValueKind.String), field + "/priority"),
            ReadScope(Property(value, "scope", field, JsonValueKind.String), field + "/scope"));
    }

    private static void WritePriority(Utf8JsonWriter writer, TargetPriority priority) => writer.WriteString("priority", priority switch
    {
        TargetPriority.ArmedFirst => "armed_first",
        TargetPriority.LowestDurability => "lowest_durability",
        TargetPriority.HighestDurability => "highest_durability",
        _ => throw new ArgumentException("目标优先级无效。", nameof(priority))
    });

    private static TargetPriority ReadPriority(JsonElement value, string field) => Text(value, field) switch
    {
        "armed_first" => TargetPriority.ArmedFirst,
        "lowest_durability" => TargetPriority.LowestDurability,
        "highest_durability" => TargetPriority.HighestDurability,
        _ => throw new InvalidField(field)
    };

    private static void WriteScope(Utf8JsonWriter writer, TargetScope scope) => writer.WriteString("scope", scope switch
    {
        TargetScope.ArmedOnly => "armed_only",
        TargetScope.All => "all",
        _ => throw new ArgumentException("目标范围无效。", nameof(scope))
    });

    private static TargetScope ReadScope(JsonElement value, string field) => Text(value, field) switch
    {
        "armed_only" => TargetScope.ArmedOnly,
        "all" => TargetScope.All,
        _ => throw new InvalidField(field)
    };

    private static void WriteController(Utf8JsonWriter writer, CombatController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        writer.WriteStartObject("controller");
        switch (controller)
        {
            case CombatController.Train train:
                writer.WriteString("kind", "train");
                WriteIdentity(writer, "train", train.Id.Value);
                break;
            case CombatController.Settlement settlement:
                writer.WriteString("kind", "settlement");
                WriteIdentity(writer, "settlement", settlement.Id.Value);
                break;
            default: throw new ArgumentException("战斗控制方无效。", nameof(controller));
        }
        writer.WriteEndObject();
    }

    private static CombatController ReadController(JsonElement value)
    {
        const string field = "/controller";
        var kind = Text(Property(value, "kind", field, JsonValueKind.String), field + "/kind");
        if (kind is not ("train" or "settlement")) throw new InvalidField(field + "/kind");
        var name = kind == "train" ? "train" : "settlement";
        Fields(value, field, ["kind", name]);
        var id = Identity(Property(value, name, field, JsonValueKind.Number), field + "/" + name);
        return kind == "train" ? new CombatController.Train(new(id)) : new CombatController.Settlement(new(id));
    }

    private static void WriteWorldObject(Utf8JsonWriter writer, string name, WorldObjectId value)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject(name);
        switch (value)
        {
            case WorldObjectId.Node node:
                writer.WriteString("kind", "node");
                WriteIdentity(writer, "node", node.Id.Value);
                break;
            case WorldObjectId.Edge edge:
                writer.WriteString("kind", "edge");
                WriteIdentity(writer, "edge", edge.Id.Value);
                break;
            case WorldObjectId.Train train:
                writer.WriteString("kind", "train");
                WriteIdentity(writer, "train", train.Id.Value);
                break;
            case WorldObjectId.Instance instance:
                writer.WriteString("kind", "instance");
                writer.WriteStartObject("instance");
                switch (instance.Id)
                {
                    case InstanceId.Vehicle vehicle:
                        writer.WriteString("kind", "vehicle");
                        WriteIdentity(writer, "vehicle", vehicle.Id.Value);
                        break;
                    case InstanceId.Building building:
                        writer.WriteString("kind", "building");
                        WriteIdentity(writer, "building", building.Id.Value);
                        break;
                    default: throw new ArgumentException("实例身份无效。", nameof(value));
                }
                writer.WriteEndObject();
                break;
            case WorldObjectId.Settlement settlement:
                writer.WriteString("kind", "settlement");
                WriteIdentity(writer, "settlement", settlement.Id.Value);
                break;
            case WorldObjectId.Inventory inventory:
                writer.WriteString("kind", "inventory");
                WriteIdentity(writer, "inventory", inventory.Id.Value);
                break;
            case WorldObjectId.Job job:
                writer.WriteString("kind", "job");
                WriteIdentity(writer, "job", job.Id.Value);
                break;
            case WorldObjectId.Combat combat:
                writer.WriteString("kind", "combat");
                WriteIdentity(writer, "combat", combat.Id.Value);
                break;
            case WorldObjectId.Creature creature:
                writer.WriteString("kind", "creature");
                WriteIdentity(writer, "creature", creature.Id.Value);
                break;
            case WorldObjectId.CreatureGroup group:
                writer.WriteString("kind", "creature_group");
                WriteIdentity(writer, "creature_group", group.Id.Value);
                break;
            case WorldObjectId.Lair lair:
                writer.WriteString("kind", "lair");
                WriteIdentity(writer, "lair", lair.Id.Value);
                break;
            default: throw new ArgumentException("世界对象无效。", nameof(value));
        }
        writer.WriteEndObject();
    }

    private static WorldObjectId ReadWorldObject(JsonElement value, string field)
    {
        var kind = Text(Property(value, "kind", field, JsonValueKind.String), field + "/kind");
        if (kind is not ("node" or "edge" or "train" or "instance" or "settlement" or "inventory" or "job"
            or "combat" or "creature" or "creature_group" or "lair")) throw new InvalidField(field + "/kind");
        if (kind == "instance")
        {
            Fields(value, field, ["kind", "instance"]);
            var instance = Property(value, "instance", field, JsonValueKind.Object);
            var instanceKind = Text(Property(instance, "kind", field + "/instance", JsonValueKind.String), field + "/instance/kind");
            if (instanceKind is not ("vehicle" or "building")) throw new InvalidField(field + "/instance/kind");
            var name = instanceKind == "vehicle" ? "vehicle" : "building";
            Fields(instance, field + "/instance", ["kind", name]);
            var id = Identity(Property(instance, name, field + "/instance", JsonValueKind.Number), field + "/instance/" + name);
            return new WorldObjectId.Instance(instanceKind == "vehicle"
                ? new InstanceId.Vehicle(new(id)) : new InstanceId.Building(new(id)));
        }
        Fields(value, field, ["kind", kind]);
        var identity = Identity(Property(value, kind, field, JsonValueKind.Number), field + "/" + kind);
        return kind switch
        {
            "node" => new WorldObjectId.Node(new(identity)),
            "edge" => new WorldObjectId.Edge(new(identity)),
            "train" => new WorldObjectId.Train(new(identity)),
            "settlement" => new WorldObjectId.Settlement(new(identity)),
            "inventory" => new WorldObjectId.Inventory(new(identity)),
            "job" => new WorldObjectId.Job(new(identity)),
            "combat" => new WorldObjectId.Combat(new(identity)),
            "creature" => new WorldObjectId.Creature(new(identity)),
            "creature_group" => new WorldObjectId.CreatureGroup(new(identity)),
            "lair" => new WorldObjectId.Lair(new(identity)),
            _ => throw new InvalidField(field + "/kind")
        };
    }

    private static double Double(JsonElement value, string field)
    {
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidField(field);
        return number;
    }

    private static void WritePositive(Utf8JsonWriter writer, string name, long value)
    {
        if (value <= 0) throw new ArgumentException("数量必须为正。", name);
        writer.WriteNumber(name, value);
    }

    private static void WriteNonnegativeInteger(Utf8JsonWriter writer, string name, long value)
    {
        if (value < 0) throw new ArgumentException("整数必须非负。", name);
        writer.WriteNumber(name, value);
    }

    private static long ReadNonnegativeInteger(JsonElement root, string name, string field = "")
    {
        var value = Property(root, name, field, JsonValueKind.Number);
        if (!value.TryGetInt64(out var number) || number < 0) throw new InvalidField(field + "/" + name);
        return number;
    }

    private static long Positive(JsonElement value, string field)
    {
        if (!value.TryGetInt64(out var number) || number <= 0) throw new InvalidField(field);
        return number;
    }

    private static RunPlan Plan(JsonElement plan)
    {
        Fields(plan, "/plan", ["loop", "stops"]);
        if (!plan.TryGetProperty("loop", out var loop) || loop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidField("/plan/loop");
        var stops = ImmutableArray.CreateBuilder<PlanStop>();
        foreach (var stop in Property(plan, "stops", "/plan", JsonValueKind.Array).EnumerateArray())
        {
            var field = $"/plan/stops/{stops.Count}";
            var kind = Text(Property(stop, "kind", field, JsonValueKind.String), field + "/kind");
            if (kind is not ("waypoint" or "station")) throw new InvalidField(field + "/kind");
            Fields(stop, field, kind == "waypoint" ? ["kind", "node"] : ["kind", "node", "dwell_steps", "goods_rules"]);
            var node = new NodeId(Identity(Property(stop, "node", field, JsonValueKind.Number), field + "/node"));
            if (kind == "waypoint") stops.Add(new PlanStop.Waypoint(node));
            else
            {
                var dwell = Number(Property(stop, "dwell_steps", field, JsonValueKind.Number), field + "/dwell_steps");
                var rules = ImmutableArray.CreateBuilder<AutomaticGoodsRule>();
                foreach (var rule in Property(stop, "goods_rules", field, JsonValueKind.Array).EnumerateArray())
                    rules.Add(ReadGoodsRule(rule, $"{field}/goods_rules/{rules.Count}"));
                stops.Add(new PlanStop.Station(node, dwell, rules.ToImmutable()));
            }
        }
        return new(loop.GetBoolean(), stops.ToImmutable());
    }

    private static void WriteGoodsRule(Utf8JsonWriter writer, AutomaticGoodsRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        writer.WriteStartObject();
        writer.WriteStartObject("action");
        switch (rule.Action)
        {
            case AutomaticGoodsAction.Buy buy:
                writer.WriteString("kind", "buy");
                if (buy.MaximumUnitPrice is { } maximum) WriteNonnegativeInteger(writer, "maximum_unit_price", maximum.Value);
                else writer.WriteNull("maximum_unit_price");
                break;
            case AutomaticGoodsAction.Sell sell:
                writer.WriteString("kind", "sell");
                if (sell.MinimumUnitPrice is { } minimum) WriteNonnegativeInteger(writer, "minimum_unit_price", minimum.Value);
                else writer.WriteNull("minimum_unit_price");
                break;
            case AutomaticGoodsAction.Load: writer.WriteString("kind", "load"); break;
            case AutomaticGoodsAction.Unload: writer.WriteString("kind", "unload"); break;
            default: throw new ArgumentException("自动货物动作无效。", nameof(rule));
        }
        writer.WriteEndObject();
        WriteGood(writer, rule.Good);
        WriteIdentity(writer, "local_inventory", rule.LocalInventory.Value);
        WriteNonnegativeInteger(writer, "target_quantity", rule.TargetQuantity);
        writer.WriteEndObject();
    }

    private static AutomaticGoodsRule ReadGoodsRule(JsonElement value, string field)
    {
        Fields(value, field, ["action", "good", "local_inventory", "target_quantity"]);
        var actionField = field + "/action";
        var action = Property(value, "action", field, JsonValueKind.Object);
        var kind = Text(Property(action, "kind", actionField, JsonValueKind.String), actionField + "/kind");
        if (kind is not ("buy" or "sell" or "load" or "unload")) throw new InvalidField(actionField + "/kind");
        Fields(action, actionField, kind switch
        {
            "buy" => ["kind", "maximum_unit_price"],
            "sell" => ["kind", "minimum_unit_price"],
            _ => ["kind"]
        });
        Money? price = null;
        if (kind is "buy" or "sell")
        {
            var name = kind == "buy" ? "maximum_unit_price" : "minimum_unit_price";
            if (!action.TryGetProperty(name, out var limit)) throw new InvalidField(actionField + "/" + name);
            if (limit.ValueKind != JsonValueKind.Null) price = new(ReadNonnegativeInteger(action, name, actionField));
        }
        AutomaticGoodsAction decodedAction = kind switch
        {
            "buy" => new AutomaticGoodsAction.Buy(price),
            "sell" => new AutomaticGoodsAction.Sell(price),
            "load" => new AutomaticGoodsAction.Load(),
            _ => new AutomaticGoodsAction.Unload()
        };
        var good = ReadGood(Property(value, "good", field, JsonValueKind.Object), field + "/good");
        var inventory = new InventoryId(Identity(Property(value, "local_inventory", field, JsonValueKind.Number), field + "/local_inventory"));
        return new(decodedAction, good, inventory, ReadNonnegativeInteger(value, "target_quantity", field));
    }

    private static void Fields(JsonElement value, string field, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidField(field);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var name = property.Name;
            if (!allowed.Contains(name) || !seen.Add(name))
                throw new InvalidField(field + "/" + name.Replace("~", "~0").Replace("/", "~1"));
        }
    }

    private static JsonElement Property(JsonElement value, string name, string field, JsonValueKind kind)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != kind)
            throw new InvalidField(field + "/" + name);
        return property;
    }

    private static void WriteIdentity(Utf8JsonWriter writer, string name, ulong value)
    {
        if (value == 0) throw new ArgumentException("世界身份必须非零。", name);
        writer.WriteNumber(name, value);
    }

    private static ulong Identity(JsonElement value, string field)
    {
        var number = Number(value, field);
        if (number == 0) throw new InvalidField(field);
        return number;
    }

    private static ulong Number(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number)) throw new InvalidField(field);
        return number;
    }

    private static string? Text(JsonElement value, string field)
    {
        try { return value.GetString(); }
        catch (InvalidOperationException) { throw new InvalidField(field); }
    }

    private sealed class InvalidField(string field) : Exception
    {
        internal string Field { get; } = field;
    }

    private static bool ValidIdentity(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static CoreResult<DecodedCommand> Success(DecodedCommand command) => new CoreResult<DecodedCommand>.Success(command);
    private static CoreResult<DecodedCommand> Failure(string code, string field)
        => new CoreResult<DecodedCommand>.Failure([new(code, field, null, [])]);
}
