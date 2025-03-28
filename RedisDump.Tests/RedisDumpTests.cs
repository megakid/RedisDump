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
        var sourceConnection = await ConnectionMultiplexer.ConnectAsync(_sourceRedisContainer.GetConnectionString());
        
        // Populate the default database (0)
        await PopulateDatabaseAsync(sourceConnection, 0);

        try
        {
            // Act - Execute the dump and restore commands
            var dumpSettings = new DumpCommandSettings
            {
                ConnectionString = _sourceRedisContainer.GetConnectionString(),
                OutputFile = _dumpFilePath,
                Verbose = true
            };
            
            var restoreSettings = new RestoreCommandSettings
            {
                ConnectionString = _targetRedisContainer.GetConnectionString(),
                InputFile = _dumpFilePath,
                Verbose = true
            };
            
            var restoreResult = await ExecuteDumpAndRestoreAsync(dumpSettings, restoreSettings);
            restoreResult.Should().Be(0, "Restore command should succeed");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Test failed with exception: {ex}");
            throw;
        }

        // Assert - Verify the data in target Redis
        _output.WriteLine("Verifying restored data...");

        var targetConnection = await ConnectionMultiplexer.ConnectAsync(_targetRedisContainer.GetConnectionString());

        // Verify the default database (0)
        await VerifyDatabaseAsync(targetConnection, 0);

        _output.WriteLine("All data verified successfully");
    }
    
    [Fact]
    public async Task MultiDatabaseDumpAndRestoreTest()
    {
        // Arrange - Connect to source Redis and add test data to multiple databases
        var sourceConnection = await ConnectionMultiplexer.ConnectAsync(_sourceRedisContainer.GetConnectionString());
        
        _output.WriteLine("Adding test data to multiple databases in source Redis...");

        // Populate databases 0-3 with test data
        for (int i1 = 0; i1 <= 3; i1++)
        {
            await PopulateDatabaseAsync(sourceConnection, i1);
        }

        _output.WriteLine("Test data added successfully to multiple databases");

        try
        {
            // Act - Execute the dump and restore commands for all databases
            var dumpSettings = new DumpCommandSettings
            {
                ConnectionString = _sourceRedisContainer.GetConnectionString(),
                OutputFile = _dumpFilePath,
                Verbose = true
            };
            
            var restoreSettings = new RestoreCommandSettings
            {
                ConnectionString = _targetRedisContainer.GetConnectionString(),
                InputFile = _dumpFilePath,
                Verbose = true
            };
            
            var restoreResult = await ExecuteDumpAndRestoreAsync(dumpSettings, restoreSettings);
            restoreResult.Should().Be(0, "Restore command should succeed");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Test failed with exception: {ex}");
            throw;
        }

        // Assert - Verify the data in target Redis across all databases
        _output.WriteLine("Verifying restored data across multiple databases...");

        var targetConnection = await ConnectionMultiplexer.ConnectAsync(_targetRedisContainer.GetConnectionString());

        // Verify databases 0-3
        for (int i = 0; i <= 3; i++)
        {
            await VerifyDatabaseAsync(targetConnection, i);
        }

        _output.WriteLine("All data verified successfully across multiple databases");
    }
    
    [Fact]
    public async Task SpecificDatabaseDumpAndRestoreTest()
    {
        int specificDatabase = 2; // We'll test with database 2

        // Arrange - Connect to source Redis and add test data to multiple databases
        var sourceConnection = await ConnectionMultiplexer.ConnectAsync(_sourceRedisContainer.GetConnectionString());
        
        _output.WriteLine("Adding test data to multiple databases in source Redis...");

        // Populate databases 0-3 with test data
        for (int i1 = 0; i1 <= 3; i1++)
        {
            await PopulateDatabaseAsync(sourceConnection, i1);
        }

        _output.WriteLine("Test data added successfully to multiple databases");

        try
        {
            // Act - Execute the dump and restore commands for a specific database
            var dumpSettings = new DumpCommandSettings
            {
                ConnectionString = _sourceRedisContainer.GetConnectionString(),
                OutputFile = _dumpFilePath,
                Databases = [specificDatabase], // Explicitly set to only use database 2
                Verbose = true
            };
            
            var restoreSettings = new RestoreCommandSettings
            {
                ConnectionString = _targetRedisContainer.GetConnectionString(),
                InputFile = _dumpFilePath,
                Databases = [specificDatabase],
                Verbose = true
            };
            
            var restoreResult = await ExecuteDumpAndRestoreAsync(dumpSettings, restoreSettings);
            restoreResult.Should().Be(0, "Restore command should succeed");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Test failed with exception: {ex}");
            throw;
        }

        // Assert - Verify only the data from database 2 was restored
        var targetConnection = await ConnectionMultiplexer.ConnectAsync(_targetRedisContainer.GetConnectionString());

        // Verify database 2 was restored correctly
        await VerifyDatabaseAsync(targetConnection, specificDatabase);

        // Verify that other databases don't have data (should be empty)
        for (int i = 0; i <= 3; i++)
        {
            if (i != specificDatabase)
            {
                var db = targetConnection.GetDatabase(i);
                string prefix = $"db{i}-";
                (await db.KeyExistsAsync($"{prefix}string1")).Should().BeFalse($"Data from database {i} should not be restored");
            }
        }

        _output.WriteLine("Database specific restore verified successfully");
    }
    
    [Fact]
    public async Task ComplexConnectionStringOptionsTest()
    {
        // Arrange - Connect to source Redis and add test data
        var sourceConnection = await ConnectionMultiplexer.ConnectAsync(_sourceRedisContainer.GetConnectionString());
        
        // Populate the default database (0)
        await PopulateDatabaseAsync(sourceConnection, 0);

        try
        {
            // Act - Execute the dump and restore commands with complex connection options
            var dumpSettings = new DumpCommandSettings
            {
                // Add additional options to connection string
                ConnectionString = $"{_sourceRedisContainer.GetConnectionString()},connectTimeout=5000,syncTimeout=3000",
                OutputFile = _dumpFilePath,
                Verbose = true
            };
            
            var restoreSettings = new RestoreCommandSettings
            {
                // Add additional options to connection string
                ConnectionString = $"{_targetRedisContainer.GetConnectionString()},connectTimeout=5000,syncTimeout=3000,allowAdmin=true",
                InputFile = _dumpFilePath,
                Verbose = true,
                Flush = true // Also test the flush option
            };
            
            var restoreResult = await ExecuteDumpAndRestoreAsync(dumpSettings, restoreSettings);
            restoreResult.Should().Be(0, "Restore command should succeed");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Test failed with exception: {ex}");
            throw;
        }

        // Assert - Verify the data in target Redis
        _output.WriteLine("Verifying restored data...");

        var targetConnection = await ConnectionMultiplexer.ConnectAsync(_targetRedisContainer.GetConnectionString());

        // Verify the default database (0)
        await VerifyDatabaseAsync(targetConnection, 0);

        _output.WriteLine("Complex connection options test completed successfully");
    }
    
    [Fact]
    public async Task RestoreFailsWhenKeysExistWithoutForceFlag()
    {
        // Arrange
        
        // Populate the default database (0)
        var sourceConnection = await ConnectionMultiplexer.ConnectAsync(_sourceRedisContainer.GetConnectionString());
        await PopulateDatabaseAsync(sourceConnection, 0);

        // Add some pre-existing data to target Redis
        var targetConnection = await ConnectionMultiplexer.ConnectAsync(_targetRedisContainer.GetConnectionString());
        var targetDb = targetConnection.GetDatabase();
        await targetDb.StringSetAsync("db0-string1", "existing-value");

        try
        {
            // Act - Execute the dump and restore commands
            var dumpSettings = new DumpCommandSettings
            {
                ConnectionString = _sourceRedisContainer.GetConnectionString(),
                OutputFile = _dumpFilePath,
                Verbose = true
            };
            
            var restoreSettings = new RestoreCommandSettings
            {
                ConnectionString = _targetRedisContainer.GetConnectionString() + ",allowAdmin=true", 
                InputFile = _dumpFilePath,
                Verbose = true,
                Force = false
            };
            
            // Execute the command and expect it to return non-zero (error)
            var restoreResult = await ExecuteDumpAndRestoreAsync(dumpSettings, restoreSettings);
            restoreResult.Should().Be(1, "Restore command should fail when keys exist without force flag");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Test failed with unexpected exception: {ex}");
            throw;
        }

        // Assert: Verify the existing key wasn't modified
        var stringValue = await targetDb.StringGetAsync("db0-string1");
        stringValue.ToString().Should().Be("existing-value", "Pre-existing key should not be modified");

        _output.WriteLine("Test verified successfully - restore failed as expected");
    }
    
    [Fact]
    public async Task RestoreSucceedsWithForceFlagWhenKeysExist()
    {
        // Arrange - Connect to source Redis and add test data
        int targetDbId = 0;
        // Populate the default database (0)
        var sourceConnection = await ConnectionMultiplexer.ConnectAsync(_sourceRedisContainer.GetConnectionString());
        await PopulateDatabaseAsync(sourceConnection, 0);

        var targetConnection = await ConnectionMultiplexer.ConnectAsync(_targetRedisContainer.GetConnectionString());
        var targetDb = targetConnection.GetDatabase(targetDbId);
        await targetDb.StringSetAsync("db0-string1", "existing-value");
        await targetDb.StringSetAsync("db0-string2", "existing-value2");

        try
        {
            // Act - Execute the dump and restore commands with force flag
            var dumpSettings = new DumpCommandSettings
            {
                ConnectionString = _sourceRedisContainer.GetConnectionString(),
                OutputFile = _dumpFilePath,
                Verbose = true
            };
            
            var restoreSettings = new RestoreCommandSettings
            {
                ConnectionString = _targetRedisContainer.GetConnectionString(),
                InputFile = _dumpFilePath,
                Verbose = true,
                Force = true
            };
            
            var restoreResult = await ExecuteDumpAndRestoreAsync(dumpSettings, restoreSettings);
            restoreResult.Should().Be(0, "Restore command should succeed with force flag");
            
            // Assert - Verify the data in target Redis was overwritten
            var string1Value = await targetDb.StringGetAsync("db0-string1");
            string1Value.ToString().Should().Be("db0-value1", "Key db0-string1 should be overwritten with new value");
            
            var string2Value = await targetDb.StringGetAsync("db0-string2");
            string2Value.ToString().Should().Be("db0-value2", "Key db0-string2 should be overwritten with new value");
            
            // Verify other restored data
            await VerifyDatabaseAsync(targetConnection, 0);
            
            _output.WriteLine("Force flag test verified successfully - data was overwritten");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Test failed with unexpected exception: {ex}");
            throw;
        }
    }
    
    [Fact]
    public async Task FlushFlagShouldRemoveExistingKeysBeforeRestore()
    {
        // Arrange - Connect to source Redis and add test data
        // Populate the default database (0)
        var sourceConnection = await ConnectionMultiplexer.ConnectAsync(_sourceRedisContainer.GetConnectionString());
        await PopulateDatabaseAsync(sourceConnection, 0);

        // Add some extra data to target Redis that doesn't exist in source
        var targetConnection = await ConnectionMultiplexer.ConnectAsync(_targetRedisContainer.GetConnectionString());
        var targetDb = targetConnection.GetDatabase();
        await targetDb.StringSetAsync("extra-key", "extra-value");

        try
        {
            // Act - Execute the dump and restore commands with flush and force flags
            var dumpSettings = new DumpCommandSettings
            {
                ConnectionString = _sourceRedisContainer.GetConnectionString(),
                OutputFile = _dumpFilePath,
                Verbose = true
            };
            
            var restoreSettings = new RestoreCommandSettings
            {
                ConnectionString = _targetRedisContainer.GetConnectionString() + ",allowAdmin=true", // Add allowAdmin to enable FLUSHDB
                InputFile = _dumpFilePath,
                Verbose = true,
                Flush = true,
                Force = true // Force is now required with Flush
            };
            
            var restoreResult = await ExecuteDumpAndRestoreAsync(dumpSettings, restoreSettings);
            restoreResult.Should().Be(0, "Restore command should succeed with flush and force flags");
            
            // Assert - Verify the extra key was removed and only restored data exists
            var extraKeyExists = await targetDb.KeyExistsAsync("extra-key");
            extraKeyExists.Should().BeFalse("Extra key should be removed by flush");
            
            // Verify restored data
            await VerifyDatabaseAsync(targetConnection, 0);
            
            _output.WriteLine("Flush flag test verified successfully - database was cleaned before restore");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Test failed with unexpected exception: {ex}");
            throw;
        }
    }
    
    [Fact]
    public void FlushWithoutForceShouldFailValidation()
    {
        // Arrange
        var settings = new RestoreCommandSettings
        {
            ConnectionString = "localhost:6379",
            InputFile = "redis-dump.json",
            Flush = true,
            Force = false
        };

        // Act
        var validationResult = settings.Validate();

        // Assert
        validationResult.Successful.Should().BeFalse("Validation should fail when flush is true but force is false");
        validationResult.Message.Should().Contain("When using --flush, you must also specify --force");
        
        _output.WriteLine("Validation correctly failed when using --flush without --force");
    }

    /// <summary>
    /// Populates a Redis database with standard test data of all supported types
    /// </summary>
    /// <param name="connection">Redis connection multiplexer</param>
    /// <param name="databaseId">The database ID to populate</param>
    private async Task PopulateDatabaseAsync(ConnectionMultiplexer connection, int databaseId)
    {
        var db = connection.GetDatabase(databaseId);
        string prefix = $"db{databaseId}-";
        
        _output.WriteLine($"Adding test data to database {databaseId}...");

        // String values
        await db.StringSetAsync($"{prefix}string1", $"{prefix}value1");
        await db.StringSetAsync($"{prefix}string2", $"{prefix}value2");
        await db.StringSetAsync($"{prefix}expiring-string", $"{prefix}value-with-ttl", TimeSpan.FromHours(1));

        // List
        await db.ListRightPushAsync($"{prefix}list1", [
            $"{prefix}item1", 
            $"{prefix}item2", 
            $"{prefix}item3"
        ]);

        // Set
        await db.SetAddAsync($"{prefix}set1", [
            $"{prefix}member1", 
            $"{prefix}member2", 
            $"{prefix}member3"
        ]);

        // Sorted set
        await db.SortedSetAddAsync($"{prefix}zset1", [
            new SortedSetEntry($"{prefix}member1", 1.0 + databaseId),
            new SortedSetEntry($"{prefix}member2", 2.0 + databaseId),
            new SortedSetEntry($"{prefix}member3", 3.0 + databaseId)
        ]);

        // Hash
        await db.HashSetAsync($"{prefix}hash1", [
            new HashEntry("field1", $"{prefix}value1"),
            new HashEntry("field2", $"{prefix}value2"),
            new HashEntry("field3", $"{prefix}value3")
        ]);
    }
    
    /// <summary>
    /// Verifies that a Redis database contains the expected standard test data
    /// </summary>
    /// <param name="connection">Redis connection multiplexer</param>
    /// <param name="databaseId">The database ID to verify</param>
    private async Task VerifyDatabaseAsync(ConnectionMultiplexer connection, int databaseId)
    {
        var db = connection.GetDatabase(databaseId);
        string prefix = $"db{databaseId}-";
        
        _output.WriteLine($"Verifying data in database {databaseId}...");
        
        // Verify string values
        (await db.StringGetAsync($"{prefix}string1")).ToString().Should().Be($"{prefix}value1");
        (await db.StringGetAsync($"{prefix}string2")).ToString().Should().Be($"{prefix}value2");
        (await db.StringGetAsync($"{prefix}expiring-string")).ToString().Should().Be($"{prefix}value-with-ttl");
        
        // Verify expiring string has TTL
        (await db.KeyTimeToLiveAsync($"{prefix}expiring-string")).Should().NotBeNull();
        
        // Verify list
        var list = await db.ListRangeAsync($"{prefix}list1");
        list.Length.Should().Be(3);
        list[0].ToString().Should().Be($"{prefix}item1");
        list[1].ToString().Should().Be($"{prefix}item2");
        list[2].ToString().Should().Be($"{prefix}item3");
        
        // Verify set
        var set = await db.SetMembersAsync($"{prefix}set1");
        set.Length.Should().Be(3);
        set.Select(x => x.ToString()).Should().BeEquivalentTo(new[] 
        { 
            $"{prefix}member1", 
            $"{prefix}member2", 
            $"{prefix}member3" 
        });
        
        // Verify sorted set
        var zset = await db.SortedSetRangeByScoreWithScoresAsync($"{prefix}zset1");
        zset.Length.Should().Be(3);
        zset[0].Element.ToString().Should().Be($"{prefix}member1");
        zset[0].Score.Should().Be(1.0 + databaseId);
        zset[1].Element.ToString().Should().Be($"{prefix}member2");
        zset[1].Score.Should().Be(2.0 + databaseId);
        zset[2].Element.ToString().Should().Be($"{prefix}member3");
        zset[2].Score.Should().Be(3.0 + databaseId);
        
        // Verify hash
        var hash = await db.HashGetAllAsync($"{prefix}hash1");
        hash.Length.Should().Be(3);
        hash.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString())
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                { "field1", $"{prefix}value1" },
                { "field2", $"{prefix}value2" },
                { "field3", $"{prefix}value3" }
            });
    }

    /// <summary>
    /// Executes the dump and restore operations with the specified settings
    /// </summary>
    /// <param name="dumpSettings">Settings for the dump command</param>
    /// <param name="restoreSettings">Settings for the restore command</param>
    /// <returns>The result code from the restore operation</returns>
    private async Task<int> ExecuteDumpAndRestoreAsync(DumpCommandSettings dumpSettings, RestoreCommandSettings restoreSettings)
    {
        _output.WriteLine("Executing Redis dump command...");
        
        var dumpCommand = new DumpCommand();
        
        // Execute dump command
        var dumpResult = await dumpCommand.ExecuteAsync(null!, dumpSettings);
        dumpResult.Should().Be(0, "Dump command should succeed");
        
        // Verify dump file was created
        File.Exists(dumpSettings.OutputFile).Should().BeTrue("Dump file should exist");
        
        _output.WriteLine("Executing Redis restore command...");
        
        var restoreCommand = new RestoreCommand();
        
        // Execute restore command
        var restoreResult = await restoreCommand.ExecuteAsync(null!, restoreSettings);
        
        return restoreResult;
    }
} 