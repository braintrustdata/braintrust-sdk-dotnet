#!/usr/bin/env bash
# Verifies that every packable project under src/ is properly registered in the
# release workflow so that new packages cannot silently be omitted from releases.
#
# This script is run as part of CI. If it fails, it means a new project was added
# to src/ but was not added to the release workflow.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RELEASE_WORKFLOW="$REPO_ROOT/.github/workflows/release.yml"
SOLUTION_FILE="$REPO_ROOT/Braintrust.Sdk.sln"

errors=0

# Discover all packable src/ projects.
# A project is packable unless it explicitly sets <IsPackable>false</IsPackable>.
packable_projects=()
for csproj in "$REPO_ROOT"/src/*/*.csproj; do
  if grep -q '<IsPackable>false</IsPackable>' "$csproj" 2>/dev/null; then
    continue
  fi
  project_name="$(basename "$(dirname "$csproj")")"
  packable_projects+=("$project_name")
done

if [[ ${#packable_projects[@]} -eq 0 ]]; then
  echo "ERROR: No packable projects found under src/. Something is wrong."
  exit 1
fi

echo "Found ${#packable_projects[@]} packable project(s) under src/:"
for p in "${packable_projects[@]}"; do
  echo "  - $p"
done
echo ""

# --- Check 1: Release workflow contains a 'dotnet pack' line for each project ---
echo "Checking release workflow: $RELEASE_WORKFLOW"
for project_name in "${packable_projects[@]}"; do
  csproj_path="src/${project_name}/${project_name}.csproj"
  if ! grep -q "$csproj_path" "$RELEASE_WORKFLOW"; then
    echo "ERROR: $csproj_path is not referenced in the release workflow."
    echo "       Add a 'dotnet pack' command for it in $RELEASE_WORKFLOW"
    errors=$((errors + 1))
  else
    echo "  OK: $project_name found in release workflow"
  fi
done
echo ""

# --- Check 2: Each project is in the solution file ---
echo "Checking solution file: $SOLUTION_FILE"
for project_name in "${packable_projects[@]}"; do
  if ! grep -q "$project_name" "$SOLUTION_FILE"; then
    echo "ERROR: $project_name is not included in the solution file."
    errors=$((errors + 1))
  else
    echo "  OK: $project_name found in solution file"
  fi
done
echo ""

# --- Check 3: Verify the release workflow's NuGet publish step references each package ---
# We look for the artifact output variable names in the publish step.
# Each package should have a find command and a push reference.
echo "Checking NuGet publish references in release workflow..."
for project_name in "${packable_projects[@]}"; do
  # The find-artifacts step should search for this package's .nupkg
  if ! grep -q "${project_name}.*\.nupkg" "$RELEASE_WORKFLOW"; then
    echo "ERROR: No artifact lookup for ${project_name} .nupkg in release workflow."
    echo "       Add it to the 'Find built artifacts' step."
    errors=$((errors + 1))
  else
    echo "  OK: ${project_name} .nupkg artifact lookup found"
  fi
done
echo ""

# --- Summary ---
if [[ $errors -gt 0 ]]; then
  echo "FAILED: Found $errors error(s). New src/ projects must be added to:"
  echo "  1. The solution file (Braintrust.Sdk.sln)"
  echo "  2. The release workflow (.github/workflows/release.yml):"
  echo "     - 'Pack NuGet packages' step (dotnet pack)"
  echo "     - 'Find built artifacts' step"
  echo "     - 'Create GitHub Release' step (upload)"
  echo "     - 'Publish to NuGet.org' step (dotnet nuget push)"
  echo "     - 'Wait for NuGet.org indexing' step (status URLs)"
  exit 1
else
  echo "All $((${#packable_projects[@]})) packable projects are properly configured for release."
fi
