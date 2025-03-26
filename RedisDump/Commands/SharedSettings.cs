using System.ComponentModel;
using Spectre.Console.Cli;

namespace RedisDump.Commands;

public class SharedSettings : CommandSettings
{
    [CommandOption("-c|--connection <CONNECTION_STRING>")]
    [Description("Redis connection string (e.g. localhost:6379)")]
    [DefaultValue("localhost:6379")]
    public string ConnectionString { get; set; } = "localhost:6379";

    [CommandOption("-d|--database <DATABASE>")]
    [Description("Redis database number (omit to process all databases)")]
    public int? Database { get; set; }

    [CommandOption("-p|--password <PASSWORD>")]
    [Description("Redis password (if required)")]
    public string? Password { get; set; }

    [CommandOption("--verbose")]
    [Description("Show verbose output")]
    public bool Verbose { get; set; }
} 