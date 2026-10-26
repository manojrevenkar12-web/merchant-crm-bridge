using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;

namespace Bridge.Alm;

public enum StepKind
{
    /// <summary>Must already exist (platform records such as sdkmessages or privileges); captures its id.</summary>
    Lookup,
    /// <summary>Created when missing, left alone when present.</summary>
    Ensure,
    /// <summary>Created when missing, updated when present (assembly content, flow definition, site map).</summary>
    Upsert,
    /// <summary>An action that is safe to repeat (AddPrivilegesRole, AddAppComponents, PublishAllXml).</summary>
    Action,
}

/// <summary>One request against the Dataverse Web API. Strings may hold {{id:step}} (an id captured by an
/// earlier step) and {{file:name}} (base64 of a file given on the command line).</summary>
public sealed record Step(string Id, StepKind Kind, string Description, string? FindPath, string? IdField, string? Path, JsonObject? Body, string? UpdatePath = null);

/// <summary>Turns the model into the ordered requests that bring an environment to it.</summary>
public static class Plan
{
    public const string DefaultIconWebResource = "953b9fac-1e5e-e611-80d6-00155ded156f";

    public static IReadOnlyList<Step> Build(DataverseModel m)
    {
        var steps = new List<Step>();
        void Add(Step s)
        {
            if (steps.Any(x => x.Id == s.Id)) throw new InvalidOperationException($"duplicate step {s.Id}");
            steps.Add(s);
        }

        // Publisher and solution: every later create carries MSCRM.SolutionUniqueName, so it lands in the solution.
        Add(new("publisher", StepKind.Ensure, $"publisher {m.PublisherUniqueName} (prefix {m.Prefix})",
            $"publishers?$select=publisherid&$filter=uniquename eq '{m.PublisherUniqueName}'", "publisherid", "publishers", new JsonObject
            {
                ["uniquename"] = m.PublisherUniqueName,
                ["friendlyname"] = m.PublisherDisplayName,
                ["customizationprefix"] = m.Prefix,
                ["customizationoptionvalueprefix"] = m.OptionValuePrefix,
            }));
        Add(new("solution", StepKind.Ensure, $"solution {m.SolutionUniqueName}",
            $"solutions?$select=solutionid&$filter=uniquename eq '{m.SolutionUniqueName}'", "solutionid", "solutions", new JsonObject
            {
                ["uniquename"] = m.SolutionUniqueName,
                ["friendlyname"] = m.SolutionDisplayName,
                ["version"] = m.SolutionVersion,
                ["publisherid@odata.bind"] = "/publishers({{id:publisher}})",
            }));

        foreach (var choice in m.Choices)
        {
            Add(new($"choice:{choice.Name}", StepKind.Ensure, $"choice {choice.Name} ({choice.Options.Count} options)",
                $"GlobalOptionSetDefinitions(Name='{choice.Name}')?$select=MetadataId", "MetadataId", "GlobalOptionSetDefinitions", new JsonObject
                {
                    ["@odata.type"] = "Microsoft.Dynamics.CRM.OptionSetMetadata",
                    ["Name"] = choice.Name,
                    ["DisplayName"] = Label(choice.DisplayName),
                    ["OptionSetType"] = "Picklist",
                    ["IsGlobal"] = true,
                    ["Options"] = new JsonArray(choice.Options.Select(o => (JsonNode)new JsonObject { ["Value"] = o.Value, ["Label"] = Label(o.Label) }).ToArray()),
                }));
        }

        foreach (var table in m.Tables)
        {
            if (!table.Standard)
            {
                var primary = table.Columns.Single(c => c.Name == table.PrimaryName);
                Add(new($"table:{table.LogicalName}", StepKind.Ensure, $"table {table.LogicalName} ({table.Ownership})",
                    $"EntityDefinitions(LogicalName='{table.LogicalName}')?$select=MetadataId", "MetadataId", "EntityDefinitions", new JsonObject
                    {
                        ["@odata.type"] = "Microsoft.Dynamics.CRM.EntityMetadata",
                        ["SchemaName"] = m.SchemaName(table.LogicalName),
                        ["DisplayName"] = Label(table.DisplayName),
                        ["DisplayCollectionName"] = Label(table.DisplayName + "s"),
                        ["Description"] = Label($"{table.DisplayName} (merchant-crm-bridge)"),
                        ["OwnershipType"] = table.Ownership,
                        ["IsActivity"] = false,
                        ["HasActivities"] = false,
                        ["HasNotes"] = false,
                        ["Attributes"] = new JsonArray(Attribute(m, primary, primaryName: true)),
                    }));
            }
            else
            {
                Add(new($"table:{table.LogicalName}", StepKind.Lookup, $"table {table.LogicalName} (standard)",
                    $"EntityDefinitions(LogicalName='{table.LogicalName}')?$select=MetadataId", "MetadataId", null, null));
            }

            foreach (var column in table.Columns.Where(c => !c.Standard && !(c.Name == table.PrimaryName && !table.Standard)))
            {
                Add(new($"column:{table.LogicalName}.{column.Name}", StepKind.Ensure, $"column {table.LogicalName}.{column.Name} ({column.Type}{(Secured(table, column) ? ", secured" : "")})",
                    $"EntityDefinitions(LogicalName='{table.LogicalName}')/Attributes(LogicalName='{column.Name}')?$select=MetadataId", "MetadataId",
                    $"EntityDefinitions(LogicalName='{table.LogicalName}')/Attributes", Attribute(m, column, primaryName: false, Secured(table, column))));
            }

            foreach (var key in table.Keys)
            {
                Add(new($"key:{table.LogicalName}.{key.Name}", StepKind.Ensure, $"alternate key {table.LogicalName}({string.Join(", ", key.Attributes)})",
                    $"EntityDefinitions(LogicalName='{table.LogicalName}')/Keys(LogicalName='{key.Name}')?$select=MetadataId", "MetadataId",
                    $"EntityDefinitions(LogicalName='{table.LogicalName}')/Keys", new JsonObject
                    {
                        ["SchemaName"] = key.Name,
                        ["DisplayName"] = Label(string.Join(", ", key.Attributes)),
                        ["KeyAttributes"] = new JsonArray(key.Attributes.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                    }));
            }
        }

        // Column security: platform-owned columns are written by the bridge only; people read them.
        foreach (var profile in m.FieldSecurityProfiles)
        {
            var pid = $"profile:{profile.Name}";
            Add(new(pid, StepKind.Ensure, $"column security profile {profile.Name}",
                $"fieldsecurityprofiles?$select=fieldsecurityprofileid&$filter=name eq '{profile.Name}'", "fieldsecurityprofileid", "fieldsecurityprofiles",
                new JsonObject { ["name"] = profile.Name, ["description"] = profile.Description }));
            foreach (var (table, column) in m.Tables.SelectMany(t => t.Columns.Where(c => Secured(t, c) && profile.Owners.Contains(c.Owner)).Select(c => (t, c))))
            {
                Add(new($"permission:{profile.Name}:{table.LogicalName}.{column.Name}", StepKind.Ensure, $"  {profile.Name}: {table.LogicalName}.{column.Name} {Grants(profile)}",
                    $"fieldpermissions?$select=fieldpermissionid&$filter=_fieldsecurityprofileid_value eq {{{{id:{pid}}}}} and entityname eq '{table.LogicalName}' and attributelogicalname eq '{column.Name}'",
                    "fieldpermissionid", "fieldpermissions", new JsonObject
                    {
                        ["entityname"] = table.LogicalName,
                        ["attributelogicalname"] = column.Name,
                        ["canread"] = profile.Read ? 4 : 0,
                        ["canupdate"] = profile.Update ? 4 : 0,
                        ["cancreate"] = profile.Create ? 4 : 0,
                        ["fieldsecurityprofileid@odata.bind"] = $"/fieldsecurityprofiles({{{{id:{pid}}}}})",
                    }));
            }
        }

        // Security roles: table privileges at organization depth.
        Add(new("businessunit", StepKind.Lookup, "root business unit", "businessunits?$select=businessunitid&$filter=_parentbusinessunitid_value eq null", "businessunitid", null, null));
        foreach (var role in m.SecurityRoles)
        {
            var rid = $"role:{role.Name}";
            Add(new(rid, StepKind.Ensure, $"security role {role.Name}",
                $"roles?$select=roleid&$filter=name eq '{role.Name}' and _businessunitid_value eq {{{{id:businessunit}}}}", "roleid", "roles",
                new JsonObject { ["name"] = role.Name, ["description"] = role.Description, ["businessunitid@odata.bind"] = "/businessunits({{id:businessunit}})" }));
            var privileges = new JsonArray();
            foreach (var (table, verbs) in role.Privileges)
            {
                foreach (var verb in verbs)
                {
                    var name = $"prv{verb}{(table == "account" ? "Account" : table)}";
                    var id = $"privilege:{name}";
                    if (!steps.Any(s => s.Id == id))
                        Add(new(id, StepKind.Lookup, $"  privilege {name}", $"privileges?$select=privilegeid&$filter=name eq '{name}'", "privilegeid", null, null));
                    privileges.Add(new JsonObject { ["Depth"] = "Global", ["PrivilegeId"] = $"{{{{id:{id}}}}}", ["BusinessUnitId"] = "{{id:businessunit}}" });
                }
            }
            Add(new($"role-privileges:{role.Name}", StepKind.Action, $"  {role.Name}: {privileges.Count} privileges",
                null, null, $"roles({{{{id:{rid}}}}})/Microsoft.Dynamics.CRM.AddPrivilegesRole", new JsonObject { ["Privileges"] = privileges }));
        }

        foreach (var env in m.EnvironmentVariables)
        {
            Add(new($"env:{env.SchemaName}", StepKind.Ensure, $"environment variable {env.SchemaName} ({env.Type})",
                $"environmentvariabledefinitions?$select=environmentvariabledefinitionid&$filter=schemaname eq '{env.SchemaName}'", "environmentvariabledefinitionid",
                "environmentvariabledefinitions", new JsonObject
                {
                    ["schemaname"] = env.SchemaName,
                    ["displayname"] = env.DisplayName,
                    ["type"] = EnvironmentVariableType(env.Type),
                    ["defaultvalue"] = env.DefaultValue,
                }));
        }

        foreach (var cr in m.ConnectionReferences)
        {
            Add(new($"connref:{cr.LogicalName}", StepKind.Ensure, $"connection reference {cr.LogicalName} ({cr.ConnectorId.Split('/')[^1]})",
                $"connectionreferences?$select=connectionreferenceid&$filter=connectionreferencelogicalname eq '{cr.LogicalName}'", "connectionreferenceid",
                "connectionreferences", new JsonObject
                {
                    ["connectionreferencelogicalname"] = cr.LogicalName,
                    ["connectionreferencedisplayname"] = cr.DisplayName,
                    ["connectorid"] = cr.ConnectorId,
                }));
        }

        // Plug-ins: the assembly (content from the build), its types, the custom API and the steps.
        const string assembly = "MerchantCrmBridge.Plugins";
        Add(new("assembly", StepKind.Upsert, $"plug-in assembly {assembly} (sandbox, database)",
            $"pluginassemblies?$select=pluginassemblyid&$filter=name eq '{assembly}'", "pluginassemblyid", "pluginassemblies", new JsonObject
            {
                ["name"] = assembly,
                ["content"] = "{{file:plugins}}",
                ["isolationmode"] = 2,
                ["sourcetype"] = 0,
            }, "pluginassemblies({{id:assembly}})"));
        foreach (var type in m.PluginSteps.Select(s => s.Plugin).Concat(m.CustomApis.Select(a => a.Plugin)).Distinct(StringComparer.Ordinal))
        {
            Add(new($"type:{type}", StepKind.Ensure, $"plug-in type {type}",
                $"plugintypes?$select=plugintypeid&$filter=typename eq '{type}'", "plugintypeid", "plugintypes", new JsonObject
                {
                    ["typename"] = type,
                    ["name"] = type,
                    ["friendlyname"] = type.Split('.')[^1],
                    ["pluginassemblyid@odata.bind"] = "/pluginassemblies({{id:assembly}})",
                }));
        }

        foreach (var api in m.CustomApis)
        {
            var aid = $"api:{api.UniqueName}";
            Add(new(aid, StepKind.Ensure, $"custom API {api.UniqueName} (bound to {api.BoundEntity})",
                $"customapis?$select=customapiid&$filter=uniquename eq '{api.UniqueName}'", "customapiid", "customapis", new JsonObject
                {
                    ["uniquename"] = api.UniqueName,
                    ["name"] = api.UniqueName,
                    ["displayname"] = api.DisplayName,
                    ["description"] = api.DisplayName,
                    ["bindingtype"] = 1,
                    ["boundentitylogicalname"] = api.BoundEntity,
                    ["isfunction"] = api.IsFunction,
                    ["isprivate"] = false,
                    ["allowedcustomprocessingsteptype"] = 0,
                    ["PluginTypeId@odata.bind"] = $"/plugintypes({{{{id:type:{api.Plugin}}}}})",
                }));
            foreach (var (name, type) in api.ResponseProperties)
            {
                Add(new($"api-response:{api.UniqueName}.{name}", StepKind.Ensure, $"  response property {name} ({type})",
                    $"customapiresponseproperties?$select=customapiresponsepropertyid&$filter=uniquename eq '{name}' and _customapiid_value eq {{{{id:{aid}}}}}",
                    "customapiresponsepropertyid", "customapiresponseproperties", new JsonObject
                    {
                        ["uniquename"] = name,
                        ["name"] = $"{api.UniqueName}.{name}",
                        ["displayname"] = name,
                        ["description"] = name,
                        ["type"] = CustomApiType(type),
                        ["CustomAPIId@odata.bind"] = $"/customapis({{{{id:{aid}}}}})",
                    }));
            }
        }

        foreach (var message in m.PluginSteps.Select(s => s.Message).Distinct(StringComparer.Ordinal))
            Add(new($"message:{message}", StepKind.Lookup, $"sdk message {message}", $"sdkmessages?$select=sdkmessageid&$filter=name eq '{message}'", "sdkmessageid", null, null));
        foreach (var (message, entity) in m.PluginSteps.Select(s => (s.Message, s.Entity)).Distinct())
            Add(new($"filter:{message}:{entity}", StepKind.Lookup, $"sdk message filter {message} of {entity}",
                $"sdkmessagefilters?$select=sdkmessagefilterid&$filter=primaryobjecttypecode eq '{entity}' and _sdkmessageid_value eq {{{{id:message:{message}}}}}", "sdkmessagefilterid", null, null));

        foreach (var step in m.PluginSteps)
        {
            var name = $"{step.Plugin}: {step.Message} of {step.Entity}";
            var sid = $"step:{name}";
            var body = new JsonObject
            {
                ["name"] = name,
                ["description"] = name,
                ["rank"] = step.Rank,
                ["mode"] = step.Mode == "Synchronous" ? 0 : 1,
                ["stage"] = Stage(step.Stage),
                ["supporteddeployment"] = 0,
                ["asyncautodelete"] = false,
                ["sdkmessageid@odata.bind"] = $"/sdkmessages({{{{id:message:{step.Message}}}}})",
                ["sdkmessagefilterid@odata.bind"] = $"/sdkmessagefilters({{{{id:filter:{step.Message}:{step.Entity}}}}})",
                ["eventhandler_plugintype@odata.bind"] = $"/plugintypes({{{{id:type:{step.Plugin}}}}})",
            };
            if (step.FilteringAttributes.Count > 0) body["filteringattributes"] = string.Join(",", step.FilteringAttributes);
            Add(new(sid, StepKind.Upsert, $"step {name} ({step.Stage}{(step.FilteringAttributes.Count > 0 ? ", filtered" : "")})",
                $"sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid&$filter=name eq '{name}'", "sdkmessageprocessingstepid", "sdkmessageprocessingsteps", body,
                $"sdkmessageprocessingsteps({{{{id:{sid}}}}})"));
            if (step.PreImage.Count > 0)
            {
                Add(new($"image:{name}", StepKind.Upsert, "  pre-image pre", $"sdkmessageprocessingstepimages?$select=sdkmessageprocessingstepimageid&$filter=_sdkmessageprocessingstepid_value eq {{{{id:{sid}}}}} and entityalias eq 'pre'",
                    "sdkmessageprocessingstepimageid", "sdkmessageprocessingstepimages", new JsonObject
                    {
                        ["name"] = "pre",
                        ["entityalias"] = "pre",
                        ["imagetype"] = 0,
                        ["messagepropertyname"] = "Target",
                        ["attributes"] = string.Join(",", step.PreImage),
                        ["sdkmessageprocessingstepid@odata.bind"] = $"/sdkmessageprocessingsteps({{{{id:{sid}}}}})",
                    }, $"sdkmessageprocessingstepimages({{{{id:image:{name}}}}})"));
            }
        }

        // The model-driven app: a site map over the tables, the tables as components, validated.
        var app = m.App;
        Add(new("sitemap", StepKind.Upsert, $"site map {app.UniqueName}",
            $"sitemaps?$select=sitemapid&$filter=sitemapnameunique eq '{app.UniqueName}'", "sitemapid", "sitemaps", new JsonObject
            {
                ["sitemapname"] = app.DisplayName,
                ["sitemapnameunique"] = app.UniqueName,
                ["isappaware"] = true,
                ["sitemapxml"] = SiteMapXml(m),
            }, "sitemaps({{id:sitemap}})"));
        Add(new("app", StepKind.Ensure, $"model-driven app {app.UniqueName}",
            $"appmodules?$select=appmoduleid&$filter=uniquename eq '{app.UniqueName}'", "appmoduleid", "appmodules", new JsonObject
            {
                ["name"] = app.DisplayName,
                ["uniquename"] = app.UniqueName,
                ["description"] = app.Description,
                ["webresourceid"] = DefaultIconWebResource,
            }));
        var components = new JsonArray { new JsonObject { ["@odata.type"] = "Microsoft.Dynamics.CRM.sitemap", ["sitemapid"] = "{{id:sitemap}}" } };
        foreach (var t in app.Areas.SelectMany(a => a.Groups).SelectMany(g => g.Tables).Distinct(StringComparer.Ordinal))
            components.Add(new JsonObject { ["@odata.type"] = "Microsoft.Dynamics.CRM.entity", ["entityid"] = $"{{{{id:table:{t}}}}}" });
        Add(new("app-components", StepKind.Action, $"  app components: site map and {components.Count - 1} tables", null, null, "AddAppComponents", new JsonObject
        {
            ["AppId"] = "{{id:app}}",
            ["Components"] = components,
        }));

        foreach (var flow in m.Flows)
        {
            Add(new($"flow:{flow.UniqueName}", StepKind.Upsert, $"cloud flow {flow.UniqueName} (draft until its connections are set)",
                $"workflows?$select=workflowid&$filter=category eq 5 and name eq '{flow.Name}'", "workflowid", "workflows", new JsonObject
                {
                    ["name"] = flow.Name,
                    ["category"] = 5,
                    ["type"] = 1,
                    ["primaryentity"] = "none",
                    ["clientdata"] = System.Text.Json.Nodes.JsonNode.Parse(m.ReadText(flow.Definition))!.ToJsonString(),
                }, $"workflows({{{{id:flow:{flow.UniqueName}}}}})"));
        }

        Add(new("publish", StepKind.Action, "publish all customizations", null, null, "PublishAllXml", new JsonObject()));
        return steps;
    }

    /// <summary>Platform-owned columns on the account are secured: only the bridge writes them. The CRM version
    /// (crm-system) is not: the outbox plug-in sets it from the stored row inside the caller's own update.</summary>
    public static bool Secured(Table table, Column column) => column.Owner == "platform" && table.Standard;

    private static string Grants(FieldSecurityProfile p) =>
        string.Join("/", new[] { p.Read ? "read" : null, p.Update ? "update" : null, p.Create ? "create" : null }.Where(x => x is not null));

    public static int Stage(string stage) => stage switch
    {
        "PreValidation" => 10,
        "PreOperation" => 20,
        "PostOperation" => 40,
        _ => throw new ArgumentException($"unknown stage {stage}", nameof(stage)),
    };

    public static int EnvironmentVariableType(string type) => type switch
    {
        "String" => 100000000,
        "Number" => 100000001,
        "Boolean" => 100000002,
        "JSON" => 100000003,
        "Secret" => 100000005,
        _ => throw new ArgumentException($"unknown environment variable type {type}", nameof(type)),
    };

    public static int CustomApiType(string type) => type switch
    {
        "boolean" => 0,
        "datetime" => 1,
        "decimal" => 2,
        "integer" => 7,
        "string" => 10,
        "guid" => 12,
        _ => throw new ArgumentException($"unknown custom API type {type}", nameof(type)),
    };

    public static JsonObject Label(string text) => new()
    {
        ["@odata.type"] = "Microsoft.Dynamics.CRM.Label",
        ["LocalizedLabels"] = new JsonArray(new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.LocalizedLabel",
            ["Label"] = text,
            ["LanguageCode"] = 1033,
        }),
    };

    private static JsonObject Attribute(DataverseModel m, Column c, bool primaryName, bool secured = false)
    {
        var a = new JsonObject
        {
            ["SchemaName"] = m.SchemaName(c.Name),
            ["DisplayName"] = Label(c.DisplayName ?? Humanise(c.Name, m.Prefix)),
            ["RequiredLevel"] = new JsonObject { ["Value"] = primaryName ? "ApplicationRequired" : "None" },
        };
        if (secured) a["IsSecured"] = true;
        switch (c.Type)
        {
            case "string":
                a["@odata.type"] = "Microsoft.Dynamics.CRM.StringAttributeMetadata";
                a["MaxLength"] = c.MaxLength ?? 100;
                a["FormatName"] = new JsonObject { ["Value"] = c.Name.Contains("email", StringComparison.Ordinal) ? "Email" : "Text" };
                if (primaryName) a["IsPrimaryName"] = true;
                break;
            case "memo":
                a["@odata.type"] = "Microsoft.Dynamics.CRM.MemoAttributeMetadata";
                a["MaxLength"] = c.MaxLength ?? 2000;
                a["Format"] = "TextArea";
                break;
            case "int":
                a["@odata.type"] = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata";
                a["Format"] = "None";
                a["MinValue"] = 0;
                a["MaxValue"] = int.MaxValue;
                break;
            case "bigint":
                a["@odata.type"] = "Microsoft.Dynamics.CRM.BigIntAttributeMetadata";
                break;
            case "decimal":
                a["@odata.type"] = "Microsoft.Dynamics.CRM.DecimalAttributeMetadata";
                a["Precision"] = c.Precision ?? 2;
                a["MinValue"] = 0;
                a["MaxValue"] = 100000000000m;
                break;
            case "datetime":
                a["@odata.type"] = "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata";
                a["Format"] = "DateAndTime";
                a["DateTimeBehavior"] = new JsonObject { ["Value"] = "TimeZoneIndependent" };
                break;
            case "choice":
                a["@odata.type"] = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata";
                a["GlobalOptionSet@odata.bind"] = $"/GlobalOptionSetDefinitions({{{{id:choice:{c.Choice}}}}})";
                break;
            default:
                throw new InvalidOperationException($"column type {c.Type} of {c.Name} has no mapping");
        }
        return a;
    }

    private static string Humanise(string logicalName, string prefix)
    {
        var s = logicalName.StartsWith(prefix + "_", StringComparison.Ordinal) ? logicalName[(prefix.Length + 1)..] : logicalName;
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    public static string SiteMapXml(DataverseModel m)
    {
        static string T(string title) => $"<Titles><Title LCID=\"1033\" Title=\"{SecurityElement.Escape(title)}\" /></Titles>";
        var sb = new StringBuilder("<SiteMap IntroducedVersion=\"7.0.0.0\">");
        var areaIndex = 0;
        foreach (var area in m.App.Areas)
        {
            areaIndex++;
            sb.Append(CultureInfo.InvariantCulture, $"<Area Id=\"mcb_area{areaIndex}\" ShowGroups=\"true\">{T(area.Title)}");
            var groupIndex = 0;
            foreach (var (title, tables) in area.Groups)
            {
                groupIndex++;
                sb.Append(CultureInfo.InvariantCulture, $"<Group Id=\"mcb_group{areaIndex}_{groupIndex}\">{T(title)}");
                foreach (var t in tables)
                {
                    var table = m.Tables.Single(x => x.LogicalName == t);
                    sb.Append(CultureInfo.InvariantCulture, $"<SubArea Id=\"mcb_sub_{t}\" Entity=\"{t}\">{T(t == "account" ? "Merchants" : table.DisplayName + "s")}</SubArea>");
                }
                sb.Append("</Group>");
            }
            sb.Append("</Area>");
        }
        return sb.Append("</SiteMap>").ToString();
    }
}
