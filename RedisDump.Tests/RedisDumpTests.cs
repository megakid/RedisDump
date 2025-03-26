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
} 