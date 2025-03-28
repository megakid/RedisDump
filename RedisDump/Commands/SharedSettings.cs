using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;


namespace RedisDump.Commands;

public class SharedSettings : CommandSettings
{
    [CommandOption("-c|--connection <CONNECTION_STRING>")]
    [Description("Redis connection string (e.g. localhost:6379,password=secret,ssl=true)")]
    [DefaultValue("localhost:6379")]
    public string ConnectionString { get; set; } = "localhost:6379";

    [CommandOption("-d|--database <DATABASE_NUMBER>")]
    [Description("Redis database numbers to process (can be specified multiple times). If not specified, all databases will be processed.")]
    public int[] Databases { get; set; } = [];

    [CommandOption("--verbose")]
    [Description("Show verbose output")]
    public bool Verbose { get; set; }

    public override ValidationResult Validate()
    {
        // If flush is enabled, force must also be enabled
        if (Databases.Any(i => i < 0))
        {
            return ValidationResult.Error("Database numbers cannot be negative");
        }

        return base.Validate();
    }
} 