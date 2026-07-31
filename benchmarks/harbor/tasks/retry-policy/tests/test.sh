#!/usr/bin/env bash

set -u

dotnet run --project /tests/Verifier/Verifier.csproj --configuration Release
status=$?

if [ "$status" -eq 0 ]; then
    echo 1 > /logs/verifier/reward.txt
else
    echo 0 > /logs/verifier/reward.txt
fi

exit "$status"
