# Redis Dump Tool

A .NET command-line tool for dumping and restoring Redis data.

## Installation

```bash
# Install globally
dotnet tool install --global RedisDump

# Install locally in the current directory
dotnet tool install --local RedisDump
```

## Usage

### Dump Redis Data

Dump all data from Redis to a file:

```bash
redis-dump dump --connection "localhost:6379" --output "redis-backup.json"
```

Dump a specific database:

```bash
redis-dump dump --connection "localhost:6379" --database 0 --output "redis-backup.json"
```

Dump multiple databases:

```bash
redis-dump dump --connection "localhost:6379" --database 0 --database 1 --database 2 --output "redis-backup.json"
```

Options:
- `-c, --connection <CONNECTION_STRING>`: Redis connection string (default: localhost:6379)
- `-d, --database <DATABASE_NUMBERS>`: Redis database numbers to process (can be specified multiple times). If not specified, all databases will be processed.
- `-o, --output <FILE>`: Output file path (default: redis-dump.json)
- `-b, --batch-size <SIZE>`: Number of keys to process in a single Lua batch (default: 100)
- `--verbose`: Show verbose output

### Restore Redis Data

Restore data from a file to Redis:

```bash
redis-dump restore --connection "localhost:6379" --input "redis-backup.json"
```

Restore to a specific database:

```bash
redis-dump restore --connection "localhost:6379" --database 0 --input "redis-backup.json"
```

Restore to multiple databases:

```bash
redis-dump restore --connection "localhost:6379" --database 0 --database 1 --database 2 --input "redis-backup.json"
```

Options:
- `-c, --connection <CONNECTION_STRING>`: Redis connection string (default: localhost:6379)
- `-d, --database <DATABASE_NUMBERS>`: Redis database numbers to process (can be specified multiple times). If not specified, all databases will be processed.
- `-i, --input <FILE>`: Input file path (default: redis-dump.json)
- `--flush`: Flush the database before restoring (requires --force)
- `-f, --force`: Force restore even if database is not empty
- `--verbose`: Show verbose output

## Features

- Dumps and restores all Redis data types (string, list, set, sorted set, hash)
- Preserves TTL values
- Supports multiple databases
- Beautiful console output with progress bars
- JSON preview with verbose mode
- Authentication support with password
- Batch processing for improved performance

## Example

```bash
# Dump all Redis data
redis-dump dump --connection "redis-server:6379,password=mypassword,ssl=true" --output "backup.json" --verbose --batch-size 200

# Restore data to a new Redis server
redis-dump restore --connection "new-redis-server:6379,password=newpassword,allowAdmin=true" --input "backup.json" --flush --force
```

## Connection String Format

The connection string format follows the StackExchange.Redis format:
```
server:port,option1=value1,option2=value2
```

Common options:
- `password=secret`: Redis password
- `ssl=true`: Enable SSL
- `allowAdmin=true`: Allow commands that might be considered risky

See https://stackexchange.github.io/StackExchange.Redis/Configuration for full details

## Development

### Building from Source

```bash
git clone https://github.com/yourusername/RedisDump.git
cd RedisDump
dotnet build
```

### Creating and Installing the Tool Locally

```bash
dotnet pack
dotnet tool install --global --add-source ./RedisDump/nupkg RedisDump
```

## License

MIT 