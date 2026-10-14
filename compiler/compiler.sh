#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_FILE="$SCRIPT_DIR/PhantomHaze.csproj"
if [[ ! -f "$PROJECT_FILE" ]]; then
  PROJECT_FILE="$SCRIPT_DIR/../PhantomHaze.csproj"
fi

if [[ ! -f "$PROJECT_FILE" ]]; then
  echo "Could not find PhantomHaze.csproj in '$SCRIPT_DIR' or its parent directory." >&2
  exit 1
fi

PROJECT_DIR="$(cd "$(dirname "$PROJECT_FILE")" && pwd)"
TEMP_ROOT="${PHANTOMHAZE_TEMP_ROOT:-$SCRIPT_DIR/tmp}"
OUTPUT_DIR="${1:-$TEMP_ROOT/PhantomHaze/publish}"
LOG_DIR="$TEMP_ROOT/PhantomHaze/logs"
BIN_DIR="$TEMP_ROOT/PhantomHaze/bin"
OBJ_DIR="$TEMP_ROOT/PhantomHaze/obj"
EXE_PATH="$OUTPUT_DIR/PhantomHaze.exe"
LOG_FILE="$LOG_DIR/compiler-$(date -u +%Y%m%dT%H%M%SZ).log"
LEGACY_BIN_DIR="$PROJECT_DIR/bin"
LEGACY_OBJ_DIR="$PROJECT_DIR/obj"
PROJECT_LOCAL_TEMP_ROOT="$SCRIPT_DIR/tmp"

mkdir -p "$LOG_DIR"
mkdir -p "$BIN_DIR"
mkdir -p "$OBJ_DIR"
touch "$LOG_FILE"

print_log_links_on_exit() {
  local exit_code="$1"

  if [[ "$exit_code" -eq 0 ]]; then
    echo "Build complete: $EXE_PATH"
  else
    echo "Build failed with exit code $exit_code."
  fi

  echo "Compiled EXE path: $EXE_PATH"
  echo "Compiled EXE URI: file://$EXE_PATH"
  echo "Compiler log: $LOG_FILE"
  echo "Compiler log URI: file://$LOG_FILE"
  echo "Runtime logs directory: $LOG_DIR"
  echo "Runtime logs URI: file://$LOG_DIR"
}

on_exit() {
  local exit_code=$?
  print_log_links_on_exit "$exit_code"
}

trap on_exit EXIT
exec > >(tee -a "$LOG_FILE") 2>&1

DOTNET_CMD="dotnet"
if ! command -v "$DOTNET_CMD" >/dev/null 2>&1; then
  if command -v dotnet.exe >/dev/null 2>&1; then
    DOTNET_CMD="dotnet.exe"
  elif [[ -x "/mnt/c/Program Files/dotnet/dotnet.exe" ]]; then
    DOTNET_CMD="/mnt/c/Program Files/dotnet/dotnet.exe"
  else
    echo "Error: dotnet SDK is not installed or not on PATH." >&2
    exit 1
  fi
fi

echo "Using .NET SDK $($DOTNET_CMD --version)"
echo "Publishing $PROJECT_FILE to $OUTPUT_DIR"
echo "Temp build paths: bin=$BIN_DIR obj=$OBJ_DIR"

PROJECT_FILE_ARG="$PROJECT_FILE"
OUTPUT_DIR_ARG="$OUTPUT_DIR"
BIN_DIR_ARG="$BIN_DIR/"
OBJ_DIR_ARG="$OBJ_DIR/"

if [[ "$DOTNET_CMD" == *.exe ]] && command -v wslpath >/dev/null 2>&1; then
  PROJECT_FILE_ARG="$(wslpath -w "$PROJECT_FILE")"
  OUTPUT_DIR_ARG="$(wslpath -w "$OUTPUT_DIR")"
  BIN_DIR_ARG="$(wslpath -w "$BIN_DIR")\\"
  OBJ_DIR_ARG="$(wslpath -w "$OBJ_DIR")\\"
fi

if [[ -d "$LEGACY_BIN_DIR" || -d "$LEGACY_OBJ_DIR" ]]; then
  echo "Cleaning project-root bin/obj to avoid duplicate compile items with custom temp paths."
  rm -rf "$LEGACY_BIN_DIR" "$LEGACY_OBJ_DIR"
fi

rm -rf "$BIN_DIR" "$OBJ_DIR" "$OUTPUT_DIR"
mkdir -p "$BIN_DIR" "$OBJ_DIR" "$OUTPUT_DIR"

if [[ -d "$PROJECT_LOCAL_TEMP_ROOT" ]]; then
  PROJECT_LOCAL_TEMP_ROOT_FULL="$(cd "$PROJECT_LOCAL_TEMP_ROOT" && pwd)"
  CURRENT_TEMP_ROOT_FULL="$(cd "$TEMP_ROOT" && pwd)"
  PRESERVED_TEMP_CHILD_DIR_FULL=""

  if [[ "$CURRENT_TEMP_ROOT_FULL" == "$PROJECT_LOCAL_TEMP_ROOT_FULL" ]]; then
    PRESERVED_TEMP_CHILD_DIR_FULL="$PROJECT_LOCAL_TEMP_ROOT_FULL/PhantomHaze"
  elif [[ "$CURRENT_TEMP_ROOT_FULL" == "$PROJECT_LOCAL_TEMP_ROOT_FULL/"* ]]; then
    RELATIVE_TEMP_ROOT_FROM_PROJECT_TMP="${CURRENT_TEMP_ROOT_FULL#"$PROJECT_LOCAL_TEMP_ROOT_FULL/"}"
    ACTIVE_TOP_LEVEL_CHILD="${RELATIVE_TEMP_ROOT_FROM_PROJECT_TMP%%/*}"
    if [[ -n "$ACTIVE_TOP_LEVEL_CHILD" ]]; then
      PRESERVED_TEMP_CHILD_DIR_FULL="$PROJECT_LOCAL_TEMP_ROOT_FULL/$ACTIVE_TOP_LEVEL_CHILD"
    fi
  fi

  if [[ -n "$PRESERVED_TEMP_CHILD_DIR_FULL" ]]; then
    find "$PROJECT_LOCAL_TEMP_ROOT" -mindepth 1 -maxdepth 1 -type d -print0 | while IFS= read -r -d '' stale_dir; do
      stale_dir_full="$(cd "$stale_dir" && pwd)"
      if [[ "$stale_dir_full" != "$PRESERVED_TEMP_CHILD_DIR_FULL" ]]; then
        echo "Cleaning stale temp build root: $stale_dir"
        rm -rf "$stale_dir"
      fi
    done
  fi
fi

"$DOTNET_CMD" publish "$PROJECT_FILE_ARG" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableWindowsTargeting=true \
  -p:BaseOutputPath="$BIN_DIR_ARG" \
  -p:BaseIntermediateOutputPath="$OBJ_DIR_ARG" \
  -o "$OUTPUT_DIR_ARG"