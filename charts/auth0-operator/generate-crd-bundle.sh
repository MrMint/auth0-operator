#!/bin/bash
set -e

# Usage: generate-crd-bundle.sh <output-file>
OUTPUT_FILE="$1"
CRD_DIR="$(dirname "$0")/../../src/Alethic.Auth0.Operator/config"

# Remove existing bundle
rm -f "$OUTPUT_FILE"

# Combine all CRD files with proper YAML document separators
first=true
for crd_file in "$CRD_DIR"/*_kubernetes_auth0_com.yaml; do
    if [ "$first" = true ]; then
        first=false
        cat "$crd_file" >> "$OUTPUT_FILE"
    else
        # Add YAML document separator before each CRD with proper newlines
        printf "\n---\n" >> "$OUTPUT_FILE"
        cat "$crd_file" >> "$OUTPUT_FILE"
    fi
done

echo "Generated CRD bundle: $OUTPUT_FILE"
echo "CRDs included: $(find "$CRD_DIR" -name '*_kubernetes_auth0_com.yaml' | wc -l)"
