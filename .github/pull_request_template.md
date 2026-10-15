## What this changes

<!-- What Polaris does differently after this PR. Link the issue it fixes: "Fixes #123". -->

## How it was tested

<!-- On which host and equipment (or simulator), and what you did. "Unit tests only" is a fine answer; say so. -->

## Checklist

- [ ] `dotnet test tests/NINA.Polaris.Test/NINA.Polaris.Test.csproj` passes (or the failures were already there before this change)
- [ ] For changes under `wwwroot/`: `node scripts/check-alpine-expressions.mjs` and `node scripts/check-missing-methods.mjs` pass, and the `?v=` of a changed `app.js` or stylesheet is bumped
- [ ] New UI text has entries in `wwwroot/data/locales/`
- [ ] I read [CONTRIBUTING.md](../CONTRIBUTING.md)
