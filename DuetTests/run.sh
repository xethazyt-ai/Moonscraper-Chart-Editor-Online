#!/usr/bin/env bash
# Headless checks for Duet Mode's edit recording, without Unity.
# Compiles the chart model, edit commands and Collaboration code against small Unity stand-ins,
# then makes edits through the real command stack and checks a second copy of the song ends up identical.
# Needs Mono's C# compiler: `apt install mono-mcs mono-runtime` (Linux) or the Mono installer (Windows/macOS).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SCRIPTS="$HERE/../Moonscraper Chart Editor/Assets/Scripts"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

# Collaboration/Views draws on screen with Unity and is left out.
# mcs doesn't support the System.Enum generic constraint, so use a copy of EnumX without it
sed 's/ where EnumType : Enum//' "$SCRIPTS/Engine/Core/EnumX.cs" > "$OUT/EnumX.cs"

FILES=()
while IFS= read -r -d '' f; do FILES+=("$f"); done < <(
  find "$SCRIPTS/Game/Collaboration" "$SCRIPTS/Game/Commands" "$SCRIPTS/Engine/Commands" \
       "$SCRIPTS/Engine/Events" "$SCRIPTS/Engine/State Control" "$SCRIPTS/Game/Charts" \
       -name '*.cs' -not -path '*/Midi/*' -not -path '*/Collaboration/Views/*' -print0)
FILES+=("$SCRIPTS/External/Advanced INI Parser/INIParser.cs" "$SCRIPTS/Game/Misc/ISongValidateReport.cs"
        "$SCRIPTS/Engine/Logging/Logger.cs" "$OUT/EnumX.cs" "$HERE/UnityStubs.cs" "$HERE/ReplayTests.cs" "$HERE/SessionTests.cs" "$HERE/LinkTests.cs" "$HERE/PresenceTests.cs" "$HERE/FileTests.cs" "$HERE/Program.cs")

mcs -out:"$OUT/DuetTests.exe" -langversion:latest -nowarn:0618,0649,0414,0219,0168 -define:APPLICATION_MOONSCRAPER "${FILES[@]}"
cd "$OUT" && mono DuetTests.exe
