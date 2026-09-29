# Pass 3 follow-up-reference scenarios

Targeted live workflows for typed referents (not a statistical campaign).

    python tests/VoiceOS.Eval/Pass3Scenarios/gen_pass3.py      # writes pass3.json
    dotnet tests/VoiceOS.Eval/bin/Debug/net9.0-windows10.0.19041.0/VoiceOS.Eval.dll fixtures   # browser fixture scenarios
    dotnet <Eval.dll> live --scenarios tests/VoiceOS.Eval/Pass3Scenarios --scenario p3.other-one --out artifacts/eval/p3/x
    python tests/VoiceOS.Eval/Pass3Scenarios/show.py artifacts/eval/p3/x

Turns may carry `before` steps (focusWindow, startProcess, closeNewTabs, closeFixtureTab) that run between turns.
`closeFixtureTab` closes the foreground Chrome tab only if it is a localhost:18777 tab.
Native scenarios use only Paint and Character Map (never Notepad, which restores the user's unsaved session).
