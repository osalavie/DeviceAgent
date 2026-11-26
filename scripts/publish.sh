#!/usr/bin/env bash
# 发布 Sim.DeviceAgent.App：默认自包含 + 单文件 + win-x64，并附带示例配置。
# 用法：bash scripts/publish.sh [输出目录] [选项]
set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: publish.sh [output-dir] [options]
  -c, --configuration <name>   Build configuration (default: Release)
  -r, --runtime <rid>          Runtime identifier (default: win-x64)
  -o, --output <dir>           Output directory (default: artifacts/publish/<rid>)
      --no-single-file         Publish as a folder instead of a single exe
      --framework-dependent    Framework-dependent publish (requires .NET 10 Desktop Runtime)
      --clean                  Clear the output directory before publishing
                               (default output artifacts/publish/<rid> is always cleared)
  -h, --help                   Show this help
USAGE
}

caller_pwd="$PWD"
tool_root="$(cd "$(dirname "$0")/.." && pwd)"
project="$tool_root/src/Sim.DeviceAgent.App"
configuration="Release"
runtime="win-x64"
output=""
clean=false
single_file=true
self_contained=true

while [ "$#" -gt 0 ]; do
    case "$1" in
        -c | --configuration)
            [ "$#" -ge 2 ] || { usage >&2; exit 2; }
            configuration=$2
            shift 2
            ;;
        -r | --runtime)
            [ "$#" -ge 2 ] || { usage >&2; exit 2; }
            runtime=$2
            shift 2
            ;;
        -o | --output)
            [ "$#" -ge 2 ] || { usage >&2; exit 2; }
            output=$2
            shift 2
            ;;
        --no-single-file)
            single_file=false
            shift
            ;;
        --framework-dependent)
            self_contained=false
            shift
            ;;
        --clean)
            clean=true
            shift
            ;;
        -h | --help)
            usage
            exit 0
            ;;
        *)
            if [ "${1#-}" = "$1" ]; then
                if [ -n "$output" ]; then
                    echo "Output directory specified more than once: $1" >&2
                    exit 2
                fi
                output=$1
                shift
            else
                echo "Unknown option: $1" >&2
                usage >&2
                exit 2
            fi
            ;;
    esac
done

default_output="$tool_root/artifacts/publish/$runtime"
[ -n "$output" ] || output="$default_output"

case "$output" in
    [A-Za-z]:[\\/]*)
        drive=$(printf '%s' "${output%%:*}" | tr '[:upper:]' '[:lower:]')
        rest=${output#??}
        output="/$drive${rest//\\//}"
        ;;
    /*) ;;
    *) output="$caller_pwd/$output" ;;
esac

output_abs="$(realpath -m -- "$output")"
tool_root_abs="$(realpath -m -- "$tool_root")"
if [ "$output_abs" = "/" ]; then
    echo "Refusing to publish into: $output_abs" >&2
    exit 1
fi
case "$tool_root_abs/" in
    "$output_abs"/*)
        echo "Refusing to publish into: $output_abs" >&2
        exit 1
        ;;
esac

if [ "$(realpath -m -- "$default_output")" = "$output_abs" ]; then
    clean=true
fi

if [ -e "$output" ]; then
    if $clean; then
        echo "Cleaning $output"
        rm -rf -- "$output"
    else
        echo "Keeping existing files in $output (use --clean to clear)"
    fi
fi
mkdir -p "$output"

publish_args=("$project" -c "$configuration" -r "$runtime" -o "$output")
if $self_contained; then
    publish_args+=(--self-contained true)
else
    publish_args+=(--self-contained false)
fi
if $single_file; then
    publish_args+=(-p:PublishSingleFile=true)
fi

echo "Publishing Sim.DeviceAgent.App ($configuration/$runtime, self-contained=$self_contained, single-file=$single_file)"
dotnet publish "${publish_args[@]}"

for example in agent.example.json agent.center.example.json agent.rabbitmq.example.json; do
    cp -f -- "$project/$example" "$output/"
done

echo "Published to $output"
echo "Copy agent.example.json to agent.json next to the exe and edit it for the site."
