using FluentAssertions;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Abstractions;
using RedisDump.Commands;

namespace RedisDump.Tests;

public class RedisDumpTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly RedisContainer _sourceRedisContainer;
    private readonly RedisContainer _targetRedisContainer;
    private readonly string _dumpFilePath;

    public RedisDumpTests(ITestOutputHelper output)
    {
        _output = output;
        
        // Create test containers
        _sourceRedisContainer = new RedisBuilder()
            .WithImage("redis:latest")
            .WithPortBinding(6379, true)
            .Build();

        _targetRedisContainer = new RedisBuilder()
            .WithImage("redis:latest")
            .WithPortBinding(6380, true)
            .Build();

        // Set up temporary file path for dump
        _dumpFilePath = Path.Combine(Path.GetTempPath(), $"redis-dump-test-{Guid.NewGuid()}.json");
    }

    public async Task InitializeAsync()
    {
        // Start the Redis containers
        await _sourceRedisContainer.StartAsync();
        await _targetRedisContainer.StartAsync();
        
        _output.WriteLine($"Source Redis started at {_sourceRedisContainer.GetConnectionString()}");
        _output.WriteLine($"Target Redis started at {_targetRedisContainer.GetConnectionString()}");
    }

    public async Task DisposeAsync()
    {
        // Stop and dispose the Redis containers
        await _sourceRedisContainer.DisposeAsync();
        await _targetRedisContainer.DisposeAsync();

        // Clean up the dump file
        if (File.Exists(_dumpFilePath))
        {
            File.Delete(_dumpFilePath);
        }
    }

    [Fact]
    public async Task FullDumpAndRestoreTest()
    {
        // Arrange - Connect to source Redis and add test data of different types
        await SetupTestDataAsync();

        try
        {
            // Act - Execute the dump command
            _output.WriteLine("Executing Redis dump command...");
            
            var dumpCommand = new DumpCommand();
            var dumpSettings = new DumpCommandSettings
            {
                ConnectionString = _sourceRedisContainer.GetConnectionString(),
                OutputFile = _dumpFilePath,
                Verbose = true
            };
            
            var dumpResult = await dumpCommand.ExecuteAsync(null!, dumpSettings);
            dumpResult.Should().Be(0, "Dump command should succeed");
            
            // Verify the dump file was created
            File.Exists(_dumpFilePath).Should().BeTrue("Dump file should exist");
            
            // Execute the restore command
            _output.WriteLine("Executing Redis restore command...");
            
            var restoreCommand = new RestoreCommand();
            var restoreSettings = new RestoreCommandSettings
            {
                ConnectionString = _targetRedisContainer.GetConnectionString(),
                InputFile = _dumpFilePath,
                Verbose = true
            };
            
            var restoreResult = await restoreCommand.ExecuteAsync(null!, restoreSettings);
            restoreResult.Should().Be(0, "Restore command should succeed");
            
            // Assert - Verify the data in target Redis
            await VerifyRestoredDataAsync();
            
            _output.WriteLine("All data verified successfully");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Test failed with exception: {ex}");
            throw;
        }
    }
    
    [Fact]
    public async Task MultiDatabaseDumpAndRestoreTest()
    {
        // Arrange - Connect to source Redis and add test data to multiple databases
        await SetupMultiDatabaseTestDataAsync();

        try
        {
            // Act - Execute the dump command for all databases
            _output.WriteLine("Executing Redis dump command for multiple databases...");
            
            var dumpCommand = new DumpCommand();
            var dumpSettings = new DumpCommandSettings
            {
                ConnectionString = _sourceRedisContainer.GetConnectionString(),
                OutputFile = _dumpFilePath,
                Verbose = true
                // Database parameter is not set to dump all databases
            };
            
            var dumpResult = await dumpCommand.ExecuteAsync(null!, dumpSettings);
            dumpResult.Should().Be(0, "Dump command should succeed");
            
            // Verify the dump file was created
            File.Exists(_dumpFilePath).Should().BeTrue("Dump file should exist");
            
            // Execute the restore command for all databases
            _output.WriteLine("Executing Redis restore command for multiple databases...");
            
            var restoreCommand = new RestoreCommand();
            var restoreSettings = new RestoreCommandSettings
            {
                ConnectionString = _targetRedisContainer.GetConnectionString(),
                InputFile = _dumpFilePath,
                Verbose = true
                // Database parameter is not set to restore all databases
            };
            
            var restoreResult = await restoreCommand.ExecuteAsync(null!, restoreSettings);
            restoreResult.Should().Be(0, "Restore command should succeed");
            
            // Assert - Verify the data in target Redis across all databases
            await VerifyMultiDatabaseRestoredDataAsync();
            
            _output.WriteLine("All data verified successfully across multiple databases");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Test failed with exception: {ex}");
            throw;
        }
    }
    
    private async Task SetupTestDataAsync()
    {
        var sourceConnection = await ConnectionMultiplexer.ConnectAsync(_sourceRedisContainer.GetConnectionString());
        var sourceDb = sourceConnection.GetDatabase();

        _output.WriteLine("Adding test data to source Redis...");

        // Create string values
        await sourceDb.StringSetAsync("string1", "value1");
        await sourceDb.StringSetAsync("string2", "value2");
        await sourceDb.StringSetAsync("expiring-string", "value-with-ttl", TimeSpan.FromHours(1));

        // Create list
        await sourceDb.ListRightPushAsync("list1", new RedisValue[] { "item1", "item2", "item3" });

        // Create set
        await sourceDb.SetAddAsync("set1", new RedisValue[] { "member1", "member2", "member3" });

        // Create sorted set
        await sourceDb.SortedSetAddAsync("zset1", new[]
        {
            new SortedSetEntry("member1", 1.0),
            new SortedSetEntry("member2", 2.0),
            new SortedSetEntry("member3", 3.0)
        });

        // Create hash
        await sourceDb.HashSetAsync("hash1", new[]
        {
            new HashEntry("field1", "value1"),
            new HashEntry("field2", "value2"),
            new HashEntry("field3", "value3")
        });

        _output.WriteLine("Test data added successfully");
    }
    
    private async Task SetupMultiDatabaseTestDataAsync()
    {
        var sourceConnection = await ConnectionMultiplexer.ConnectAsync(_sourceRedisContainer.GetConnectionString());
        
        _output.WriteLine("Adding test data to multiple databases in source Redis...");

        // Database 0 (default)
        var db0 = sourceConnection.GetDatabase(0);
        await db0.StringSetAsync("db0-string1", "db0-value1");
        await db0.HashSetAsync("db0-hash1", new[] 
        {
            new HashEntry("field1", "db0-value1"),
            new HashEntry("field2", "db0-value2")
        });

        // Database 1
        var db1 = sourceConnection.GetDatabase(1);
        await db1.StringSetAsync("db1-string1", "db1-value1");
        await db1.ListRightPushAsync("db1-list1", new RedisValue[] { "db1-item1", "db1-item2" });
        
        // Database 2
        var db2 = sourceConnection.GetDatabase(2);
        await db2.SetAddAsync("db2-set1", new RedisValue[] { "db2-member1", "db2-member2" });
        await db2.SortedSetAddAsync("db2-zset1", new[]
        {
            new SortedSetEntry("db2-member1", 10.0),
            new SortedSetEntry("db2-member2", 20.0)
        });
        
        // Database 3 with expiring keys
        var db3 = sourceConnection.GetDatabase(3);
        await db3.StringSetAsync("db3-expiring-string", "db3-value-with-ttl", TimeSpan.FromHours(1));

        _output.WriteLine("Test data added successfully to multiple databases");
    }
    
    private async Task VerifyRestoredDataAsync()
    {
        _output.WriteLine("Verifying restored data...");
        
        var targetConnection = await ConnectionMultiplexer.ConnectAsync(_targetRedisContainer.GetConnectionString());
        var targetDb = targetConnection.GetDatabase();
        
        // Verify string values
        (await targetDb.StringGetAsync("string1")).ToString().Should().Be("value1");
        (await targetDb.StringGetAsync("string2")).ToString().Should().Be("value2");
        (await targetDb.StringGetAsync("expiring-string")).ToString().Should().Be("value-with-ttl");
        
        // Verify expiring string has TTL
        (await targetDb.KeyTimeToLiveAsync("expiring-string")).Should().NotBeNull();
        
        // Verify list
        var list1 = await targetDb.ListRangeAsync("list1");
        list1.Length.Should().Be(3);
        list1[0].ToString().Should().Be("item1");
        list1[1].ToString().Should().Be("item2");
        list1[2].ToString().Should().Be("item3");
        
        // Verify set
        var set1 = await targetDb.SetMembersAsync("set1");
        set1.Length.Should().Be(3);
        set1.Select(x => x.ToString()).Should().BeEquivalentTo(new[] { "member1", "member2", "member3" });
        
        // Verify sorted set
        var zset1 = await targetDb.SortedSetRangeByScoreWithScoresAsync("zset1");
        zset1.Length.Should().Be(3);
        zset1[0].Element.ToString().Should().Be("member1");
        zset1[0].Score.Should().Be(1.0);
        zset1[1].Element.ToString().Should().Be("member2");
        zset1[1].Score.Should().Be(2.0);
        zset1[2].Element.ToString().Should().Be("member3");
        zset1[2].Score.Should().Be(3.0);
        
        // Verify hash
        var hash1 = await targetDb.HashGetAllAsync("hash1");
        hash1.Length.Should().Be(3);
        hash1.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString())
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                { "field1", "value1" },
                { "field2", "value2" },
                { "field3", "value3" }
            });
    }
    
    private async Task VerifyMultiDatabaseRestoredDataAsync()
    {
        _output.WriteLine("Verifying restored data across multiple databases...");
        
        var targetConnection = await ConnectionMultiplexer.ConnectAsync(_targetRedisContainer.GetConnectionString());
        
        // Verify Database 0
        var db0 = targetConnection.GetDatabase(0);
        (await db0.StringGetAsync("db0-string1")).ToString().Should().Be("db0-value1");
        
        var db0Hash = await db0.HashGetAllAsync("db0-hash1");
        db0Hash.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString())
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                { "field1", "db0-value1" },
                { "field2", "db0-value2" }
            });
        
        // Verify Database 1
        var db1 = targetConnection.GetDatabase(1);
        (await db1.StringGetAsync("db1-string1")).ToString().Should().Be("db1-value1");
        
        var db1List = await db1.ListRangeAsync("db1-list1");
        db1List.Length.Should().Be(2);
        db1List[0].ToString().Should().Be("db1-item1");
        db1List[1].ToString().Should().Be("db1-item2");
        
        // Verify Database 2
        var db2 = targetConnection.GetDatabase(2);
        
        var db2Set = await db2.SetMembersAsync("db2-set1");
        db2Set.Length.Should().Be(2);
        db2Set.Select(x => x.ToString()).Should().BeEquivalentTo(new[] { "db2-member1", "db2-member2" });
        
        var db2ZSet = await db2.SortedSetRangeByScoreWithScoresAsync("db2-zset1");
        db2ZSet.Length.Should().Be(2);
        db2ZSet[0].Element.ToString().Should().Be("db2-member1");
        db2ZSet[0].Score.Should().Be(10.0);
        db2ZSet[1].Element.ToString().Should().Be("db2-member2");
        db2ZSet[1].Score.Should().Be(20.0);
        
        // Verify Database 3
        var db3 = targetConnection.GetDatabase(3);
        (await db3.StringGetAsync("db3-expiring-string")).ToString().Should().Be("db3-value-with-ttl");
        (await db3.KeyTimeToLiveAsync("db3-expiring-string")).Should().NotBeNull();
        
        _output.WriteLine("All data verified successfully across multiple databases");
    }
} 