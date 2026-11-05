# Code signing policy

## Status

Prompt Flipper is preparing an application to SignPath Foundation.
The application is not yet submitted or approved.
Current downloads are unsigned.

If approved, the project will use this attribution:

Free code signing provided by [SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## Responsibilities

[RC Keller](https://github.com/rckeller80) is the project's author, reviewer and release approver.
Changes from other contributors require review before integration.
Every team member must use multi-factor authentication for GitHub and SignPath access.
Every signing request requires manual approval by the release approver.

## Signing scope

Only `PromptFlipper.exe` built from the public
[Prompt Flipper repository](https://github.com/rckeller80/prompt-flipper) may receive the project's signature.
The [build workflow](../.github/workflows/build.yml) calls the supported publish script on a GitHub-hosted Windows runner.
The workflow produces an unsigned artifact until SignPath approves the application and signing is configured.
It does not publish a release or grant permission to sign third-party software.

## Privacy

Prompt Flipper makes no network calls and sends no telemetry.
It stores cards on the device and uses Windows protection for saved sanitizer rules.
The application does not send cards or saved rules to GitHub or SignPath.
Those services process project source, build artifacts and maintainer account information for the build and signing services.

## References

- [SignPath Foundation terms](https://signpath.org/terms.html)
- [GitHub .NET workflow guidance](https://docs.github.com/en/actions/tutorials/build-and-test-code/net)
