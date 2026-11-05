# Code signing policy

## Status

The portable `PromptFlipper.exe` is unsigned.
The signed route is the Microsoft Store MSIX package.
Microsoft signs the package after Store certification.

## Responsibilities

[RC Keller](https://github.com/rckeller80) is the project's author, reviewer and release approver.
Changes from other contributors require review before integration.
Every team member must use multi-factor authentication for GitHub and Microsoft Partner Center access.
Every Store submission requires manual approval by the release approver.

## Signing scope

Only the Prompt Flipper MSIX built from the public
[Prompt Flipper repository](https://github.com/rckeller80/prompt-flipper) is submitted to the Microsoft Store.
The [build workflow](../.github/workflows/build.yml) calls the supported publish script on a GitHub-hosted Windows runner and produces an unsigned artifact.
It does not publish a release or grant permission to sign third-party software.

## Privacy

Prompt Flipper makes no network calls and sends no telemetry.
It stores cards on the device and uses Windows protection for saved sanitizer rules.
See the [privacy statement](../PRIVACY.md).

## References

- [GitHub .NET workflow guidance](https://docs.github.com/en/actions/tutorials/build-and-test-code/net)
