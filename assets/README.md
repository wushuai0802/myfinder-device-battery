# Application icon

The application uses the glacier-blue dual-device design (D2). The headset and
mouse cards are the static application identity; the live tray battery remains
independent.

Editable sources:

- `app-icon.svg`: full detail for 96px and larger.
- `app-icon-compact.svg`: principal details for 40/48/64px.
- `app-icon-32.svg`: simplified headset and mouse for 32px.
- `app-icon-small.svg`: simplified shapes for 16/20/24px.

The checked-in `app-icon.ico` contains 32-bit BGRA DIB/AND frames at
16/20/24/32/40/48/64/96/128/256px for Windows and .NET Framework compatibility.
Ordinary builds do not require Node or image-rendering dependencies.

To regenerate after editing the SVGs, use Node.js with `sharp` available:

```text
node tools/Build-AppIcon.cjs [directory containing sharp] [preview directory]
```

This also refreshes `docs/images/app-icon.png`. These assets are original project
artwork distributed under the repository license.
