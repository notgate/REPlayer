#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]


def read(relative: str) -> str:
    path = ROOT / relative
    return path.read_text(encoding="utf-8-sig") if path.exists() else ""


def require(condition: bool, message: str, findings: list[str]) -> None:
    if not condition:
        findings.append(message)


def main() -> int:
    findings: list[str] = []
    center = read("ReVM/UI/Dialogs/AgentCenterDialog.xaml")
    center_code = read("ReVM/UI/Dialogs/AgentCenterDialog.xaml.cs")
    setup = read("ReVM/UI/Dialogs/AgentSetupDialog.xaml")
    inbox = read("ReVM/UI/Dialogs/AgentInboxWindow.xaml")
    evidence = read("ReVM/UI/Dialogs/AgentEvidenceWindow.xaml")
    models = read("ReVM/Automation/AiAgentModels.cs")
    credentials = read("ReVM/Automation/AgentCredentialStore.cs")
    providers = read("ReVM/Automation/AiAgentProviderClients.cs")
    runner = read("ReVM/Automation/AiAgentTaskRunner.cs")
    policy = read("ReVM/Automation/AdbAgentCommandPolicy.cs")
    probe = read("runtime-src/ReplayerAutomationProbe/Program.cs")

    require('x:Key="AgentButton"' in center and "ControlTemplate TargetType=\"Button\"" in center,
            "AgentButton must own a deterministic button template", findings)
    for state in ("IsMouseOver", "IsPressed", "IsEnabled"):
        require(state in center, f"AgentButton is missing the {state} visual state", findings)
    require('Content="Open inbox"' in center and 'Content="Open evidence"' in center and 'Content="Cancel selected"' in center,
            "Agent Center footer actions are missing", findings)
    require("ConfigureAgent_Click" in center_code and 'x:Name="TaskPromptBox"' in center,
            "Agent Center must dispatch natural-language tasks through configured agents", findings)
    require("AgentSetupDialog" in setup and "PasswordBox" in setup and "Test connection" in setup,
            "Agent setup window must collect and validate provider credentials", findings)
    require("AgentInboxWindow" in inbox and "STATUS" in inbox and "REPORT" in inbox,
            "native Agent Center inbox window is missing", findings)
    require("AgentEvidenceWindow" in evidence and "EVENT" in evidence and "DETAIL" in evidence,
            "native Agent Center evidence window is missing", findings)
    for provider in ("OpenRouter", "OpenAI", "Anthropic", "Zai"):
        require(provider in models, f"provider catalog is missing {provider}", findings)
    require("IAgentCredentialStore" in credentials and "CredWrite" in credentials and "CredRead" in credentials,
            "API keys must use a Windows Credential Manager abstraction", findings)
    require("reasoning_details" in providers and "tool_calls" in providers and "execute_adb" in providers,
            "OpenAI-compatible client must preserve opaque reasoning continuity and ADB tool calls", findings)
    require("AdbAgentCommandPolicy.IsObserveOnly(arguments" in providers and '["access"]' not in providers,
            "provider tools must derive access on the host instead of trusting model classification", findings)
    require("AllowAutoRedirect = false" in providers and "ReadBoundedBodyAsync" in providers,
            "provider transport must reject redirects and bound response bodies", findings)
    require("anthropic-version" in providers and "tool_use" in providers,
            "Anthropic Messages API support is missing", findings)
    require(all(token in runner for token in ("AiAgentTaskStatus.Incomplete", "maximumTurns", "AndroidAgentCoordinator", "RedactSecret", "ContainsSecret", "AdbAgentCommandPolicy", "taskLifetime.CancelAfter(request.Timeout)")),
            "task runner must enforce lifecycle, credential redaction, and ADB access policy", findings)
    require(all(token in policy for token in ("IsObserveOnly", "ShellMetacharacters", "requires device-control access")),
            "observe-only ADB command policy is incomplete", findings)
    require("ApiKey" not in models and "Password" not in models,
            "profile metadata models must not persist plaintext secrets", findings)
    require(all(token in probe for token in ("RunQueuedAiAgentConcurrencyProbe", "queuedAiTasks", "queuedAiSameDeviceControlMaximum", "queuedAiMixedMaximum")),
            "behavioral probe must cover queued provider-backed concurrency", findings)
    require(all(token in probe for token in ("--live-agent-queue", "RunLiveProviderAgentQueue", "sameDeviceControlsSerialized")),
            "live provider-backed Android queue probe is missing", findings)
    require("ApplyHoverPreviewForVisualProbe" in probe and "ButtonBorder" in probe,
            "hover-state render probe is missing", findings)

    if findings:
        print("AGENT_CENTER_VALIDATION_FAIL")
        for finding in findings:
            print(f"- {finding}")
        return 1
    print("AGENT_CENTER_VALIDATION_PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
