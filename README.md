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

Options:
- `-c, --connection <CONNECTION_STRING>`: Redis connection string (default: localhost:6379)
- `-d, --database <DATABASE>`: Database number (optional, omit to dump all databases)
- `-p, --password <PASSWORD>`: Redis password (if required)
- `-o, --output <FILE>`: Output file path (default: redis-dump.json)
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

Options:
- `-c, --connection <CONNECTION_STRING>`: Redis connection string (default: localhost:6379)
- `-d, --database <DATABASE>`: Database number (optional, omit to restore all databases)
- `-p, --password <PASSWORD>`: Redis password (if required)
- `-i, --input <FILE>`: Input file path (default: redis-dump.json)
- `--flush`: Flush the database before restoring
- `--verbose`: Show verbose output

## Features

- Dumps and restores all Redis data types (string, list, set, sorted set, hash)
- Preserves TTL values
- Supports multiple databases
- Beautiful console output with progress bars
- JSON preview with verbose mode
- Authentication support with password

## Example

```bash
# Dump all Redis data
redis-dump dump --connection "redis-server:6379" --password "mypassword" --output "backup.json" --verbose

# Restore data to a new Redis server
redis-dump restore --connection "new-redis-server:6379" --password "newpassword" --input "backup.json" --flush
```

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