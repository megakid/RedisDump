using System.ComponentModel;
using Spectre.Console.Cli;

namespace RedisDump.Commands;

public class SharedSettings : CommandSettings
{
    [CommandOption("-c|--connection <CONNECTION_STRING>")]
    [Description("Redis connection string (e.g. localhost:6379,password=secret,ssl=true,defaultDatabase=0)")]
    [DefaultValue("localhost:6379")]
    public string ConnectionString { get; set; } = "localhost:6379";

    [CommandOption("--verbose")]
    [Description("Show verbose output")]
    public bool Verbose { get; set; }
} 