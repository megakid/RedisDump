using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Json;
using StackExchange.Redis;

namespace RedisDump.Commands;

public record RedisKeyData
{
    public required string Type { get; init; }
    public required object Value { get; init; }
    public double? TTL { get; init; }
}

public class DumpCommandSettings : SharedSettings
{
    [CommandOption("-o|--output <FILE>")]
    [Description("Output file path")]
    [DefaultValue("redis-dump.json")]
    public string OutputFile { get; set; } = "redis-dump.json";
    
    [CommandOption("-b|--batch-size <SIZE>")]
    [Description("Number of keys to process in a single Lua batch")]
    [DefaultValue(100)]
    public int BatchSize { get; set; } = 100;
}

public class DumpCommand : AsyncCommand<DumpCommandSettings>
{
    // Lua script to get key data (type, TTL, value) in a single operation
    private const string LuaDumpScript = @"
local result = {}
for i, key in ipairs(KEYS) do
    local keyType = redis.call('TYPE', key).ok
    local ttl = redis.call('PTTL', key)
    
    local keyData = {}
    keyData.type = keyType
    keyData.ttl = ttl
    
    -- Get value based on type
    if keyType == 'string' then
        keyData.value = redis.call('GET', key)
    elseif keyType == 'list' then
        keyData.value = redis.call('LRANGE', key, 0, -1)
    elseif keyType == 'set' then
        keyData.value = redis.call('SMEMBERS', key)
    elseif keyType == 'zset' then
        local withScores = redis.call('ZRANGE', key, 0, -1, 'WITHSCORES')
        local formatted = {}
        for j = 1, #withScores, 2 do
            local member = withScores[j]
            local score = tonumber(withScores[j+1])
            table.insert(formatted, {member=member, score=score})
        end
        keyData.value = formatted
    elseif keyType == 'hash' then
        local hash = redis.call('HGETALL', key)
        local formatted = {}
        for j = 1, #hash, 2 do
            formatted[hash[j]] = hash[j+1]
        end
        keyData.value = formatted
    end
    
    result[key] = keyData
end
return cjson.encode(result)
";

    public override async Task<int> ExecuteAsync(CommandContext context, DumpCommandSettings settings)
    {
        AnsiConsole.MarkupLine("[bold blue]Redis Dump Tool[/]");
        
        try
        {
            // Connect to Redis using the full connection string
            var configOptions = ConfigurationOptions.Parse(settings.ConnectionString);
            AnsiConsole.MarkupLine($"[yellow]Connecting to Redis at {configOptions}...[/]");
            var connection = await ConnectionMultiplexer.ConnectAsync(configOptions);

            // Get server
            var server = connection.GetServer(connection.GetEndPoints().First());
            
            var result = new Dictionary<int, Dictionary<string, object>>();
            
            // Use the database parameter if specified, otherwise process all databases
            if (settings.Databases != null && settings.Databases.Length > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Dumping {settings.Databases.Length} database(s): {string.Join(", ", settings.Databases)}...[/]");
                await DumpDatabases(settings, settings.Databases, result, connection);
            }
            else
            {
                // Process all databases if Databases is null or empty
                AnsiConsole.MarkupLine("[yellow]No databases specified, dumping all databases...[/]");
                
                // Get the number of databases
                var databaseCount = server.DatabaseCount;
                if (databaseCount == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]Supported database count returned 0 (can happen with AWS ElastiCache etc), using fallback of 16[/]");
                    databaseCount = 16;
                }
                
                await DumpDatabases(settings, Enumerable.Range(0, databaseCount), result, connection);
            }
            
            // Save to file
            AnsiConsole.MarkupLine($"[yellow]Saving to {settings.OutputFile}...[/]");
            var jsonString = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(settings.OutputFile, jsonString);
            
            AnsiConsole.MarkupLine($"[green]Successfully dumped {result.Sum(db => db.Value.Count)} keys to {settings.OutputFile}[/]");
            
            // Display preview if verbose
            if (settings.Verbose)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[yellow]JSON Preview[/]").RuleStyle("grey").Centered());
                AnsiConsole.Write(new JsonText(jsonString));
                AnsiConsole.WriteLine();
            }
            
            // Dispose connection
            connection.Dispose();
            
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] {ex.Message}");
            if (settings.Verbose)
            {
                AnsiConsole.WriteException(ex);
            }
            return 1;
        }
    }

    private async Task DumpDatabases(DumpCommandSettings settings, IEnumerable<int> databases, Dictionary<int, Dictionary<string, object>> result,
        ConnectionMultiplexer connection)
    {
        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                foreach (var database in databases)
                {
                    result.Add(database, await DumpDatabaseAsync(connection, database, ctx, settings.Verbose, settings.BatchSize));
                }
            });
    }

    private async Task<Dictionary<string, object>> DumpDatabaseAsync(ConnectionMultiplexer connection,
        int databaseNumber, ProgressContext ctx, bool verbose, int batchSize)
    {
        var task = ctx.AddTask($"[green]Dumping database {databaseNumber}[/]");

        var db = connection.GetDatabase(databaseNumber);
        var servers = connection.GetServers();
        
        var result = new Dictionary<string, object>();

        // Get all keys from all servers, distinct them since if we're in replication mode, we'll get the same key from different servers
        var keys = servers.SelectMany(s => s.Keys(databaseNumber, pattern: "*"))
            .DistinctBy(x => x.ToString())
            .OrderBy(x => x.ToString())
            .ToArray();

        task.Description = $"[green]Dumping database {databaseNumber} ({keys.Length} keys)[/]";
        // Set MaxValue to at least 1 to avoid division by zero in progress calculations
        task.MaxValue = Math.Max(1, keys.Length);

        if (verbose)
        {
            AnsiConsole.MarkupLine($"[grey]Database {databaseNumber}: Found {keys.Length} keys[/]");
        }

        if (keys.Length == 0)
        {
            // For empty databases, mark the task as completed
            task.Value = task.MaxValue;
            return result;
        }

        // Process keys in batches
        for (var i = 0; i < keys.Length; i += batchSize)
        {
            // Take a batch of keys
            var batchKeys = keys.Skip(i).Take(batchSize).ToArray();
            
            if (batchKeys.Length == 0)
                continue;

            try
            {
                // Execute the Lua script with the current batch of keys
                var scriptResult = await db.ScriptEvaluateAsync(
                    LuaDumpScript,
                    batchKeys.ToArray()
                );
                
                // Parse the JSON result
                if (scriptResult.IsNull)
                {
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine($"[grey]Lua script returned null for batch starting at key {i}[/]");
                    }
                    continue;
                }
                
                var jsonResult = scriptResult.ToString();
                var doc = JsonDocument.Parse(jsonResult);
                var root = doc.RootElement;
                
                // Process each key in the batch
                foreach (var keyProperty in root.EnumerateObject())
                {
                    var key = keyProperty.Name;
                    var keyData = keyProperty.Value;
                    
                    // Get type and TTL
                    var type = keyData.GetProperty("type").GetString()?.ToLowerInvariant() ?? "";
                    
                    // Skip if type is missing
                    if (string.IsNullOrEmpty(type))
                        continue;
                    
                    // Get TTL
                    var ttl = keyData.GetProperty("ttl").GetInt64();
                    var ttlValue = ttl >= 0 ? (double?)ttl : null;
                    
                    try
                    {
                        // Process based on type
                        switch (type)
                        {
                            case "string":
                                result[key] = new RedisKeyData
                                {
                                    Type = "string",
                                    Value = keyData.GetProperty("value").GetString() ?? "",
                                    TTL = ttlValue
                                };
                                break;
                                
                            case "list":
                                var listValue = keyData.GetProperty("value").EnumerateArray()
                                    .Select(v => v.GetString() ?? "")
                                    .ToArray();
                                    
                                result[key] = new RedisKeyData
                                {
                                    Type = "list",
                                    Value = listValue,
                                    TTL = ttlValue
                                };
                                break;
                                
                            case "set":
                                var setValue = keyData.GetProperty("value").EnumerateArray()
                                    .Select(v => v.GetString() ?? "")
                                    .ToArray();
                                    
                                result[key] = new RedisKeyData
                                {
                                    Type = "set",
                                    Value = setValue,
                                    TTL = ttlValue
                                };
                                break;
                                
                            case "zset":
                                var zsetEntries = new List<object>();
                                
                                foreach (var entry in keyData.GetProperty("value").EnumerateArray())
                                {
                                    var member = entry.GetProperty("member").GetString() ?? "";
                                    var score = entry.GetProperty("score").GetDouble();
                                    
                                    zsetEntries.Add(new 
                                    { 
                                        Member = member, 
                                        Score = score 
                                    });
                                }
                                
                                result[key] = new RedisKeyData
                                {
                                    Type = "zset",
                                    Value = zsetEntries.ToArray(),
                                    TTL = ttlValue
                                };
                                break;
                                
                            case "hash":
                                var hashEntries = new Dictionary<string, string>();
                                
                                foreach (var entry in keyData.GetProperty("value").EnumerateObject())
                                {
                                    hashEntries[entry.Name] = entry.Value.GetString() ?? "";
                                }
                                
                                result[key] = new RedisKeyData
                                {
                                    Type = "hash",
                                    Value = hashEntries,
                                    TTL = ttlValue
                                };
                                break;
                                
                            default:
                                if (verbose)
                                {
                                    AnsiConsole.MarkupLine($"[grey]Skipping key {key} of unsupported type {type}[/]");
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (verbose)
                        {
                            AnsiConsole.MarkupLine($"[grey]Error processing key {key}: {ex.Message}[/]");
                        }
                    }
                }
            }
            catch (Exception luaEx)
            {
                if (verbose)
                {
                    AnsiConsole.MarkupLine($"[grey]Lua script error: {luaEx.Message}. Falling back to individual queries for this batch.[/]");
                }
                
                // Fallback: process keys individually for this batch
                await ProcessKeysBatch(db, batchKeys, result, verbose);
            }
            
            // Update progress
            task.Increment(batchKeys.Length);
        }
        
        return result;
    }

    // Fallback method for processing keys individually if the Lua script fails
    private async Task ProcessKeysBatch(IDatabase db, RedisKey[] keys, Dictionary<string, object> result, bool verbose)
    {
        foreach (var key in keys)
        {
            var keyString = key.ToString() ?? "";
            var type = db.KeyType(key);
            
            // Get TTL if exists
            var ttl = await db.KeyTimeToLiveAsync(key);
            double? ttlValue = ttl.HasValue ? ttl.Value.TotalMilliseconds : null;
            
            switch (type)
            {
                case RedisType.String:
                    var stringValue = await db.StringGetAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "string",
                        Value = stringValue.ToString(),
                        TTL = ttlValue
                    };
                    break;
                    
                case RedisType.List:
                    var listValues = await db.ListRangeAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "list",
                        Value = listValues.Select(v => v.ToString()).ToArray(),
                        TTL = ttlValue
                    };
                    break;
                    
                case RedisType.Set:
                    var setMembers = await db.SetMembersAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "set",
                        Value = setMembers.Select(v => v.ToString()).ToArray(),
                        TTL = ttlValue
                    };
                    break;
                    
                case RedisType.SortedSet:
                    var sortedSetEntries = await db.SortedSetRangeByScoreWithScoresAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "zset",
                        Value = sortedSetEntries.Select(v => new { Member = v.Element.ToString(), Score = v.Score }).ToArray(),
                        TTL = ttlValue
                    };
                    break;
                    
                case RedisType.Hash:
                    var hashEntries = await db.HashGetAllAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "hash",
                        Value = hashEntries.ToDictionary(he => he.Name.ToString(), he => he.Value.ToString()),
                        TTL = ttlValue
                    };
                    break;
                    
                default:
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine($"[grey]Skipping key {keyString} of unsupported type {type}[/]");
                    }
                    break;
            }
        }
    }
} 