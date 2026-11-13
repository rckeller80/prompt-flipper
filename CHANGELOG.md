# Changelog

## Unreleased

- Review finds UK, Dutch and international phone numbers, IBANs and UK postcodes.
- Review reports a card number only when it passes the Luhn check.
- Review never reports an IBAN as a card number or a phone number.
- Settings states which personal details Review finds. Review does not find names or street addresses.
- The Sanitizer button states that it replaces saved secrets only and does not check for personal details.

## 1.0.2

- Settings shows each secret value in full, with the placeholder name beside it.
- Hide all in Settings masks secret values and personal details on screen.
- Sort A-Z in Settings orders the secret list by value.
- Review always lists personal details that are not sanitized, with Add as secret and Add all.
- Help search highlights the matches in a topic.
- About shows the author, the source repository, the Microsoft Store listing and support links.

## 1.0.1

- Search Help topics from the Help window.
- Help follows the app layout in an Explorer-style tree, with updated text for every topic.
- Help About shows the author, version and licence.
- Reopen the folder and cards that were open when the app last closed.
- Add a card from a template file.
- Tooltips use the control name.

## 1.0.0

First version.

- Store prompts and notes as Markdown cards in folders.
- Browse cards with previous and next controls or card tabs.
- Search cards by file name, title or text.
- Edit and rename cards, with automatic saving when you change cards or close the app.
- Copy card text or replace saved secrets with placeholders in a copy.
- Adjust text size and line wrapping in the editor.
- Run a single executable on 64-bit Windows without installing .NET.
