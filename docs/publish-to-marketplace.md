# Publishing a Visual Studio extension to the Marketplace from GitHub Actions — with Entra OIDC, no PAT

End-to-end recipe, as set up for VSNeo (v1.0.0 published this way). Goal state:

- Push to `master` → CI build → Open VSIX Gallery (testing/nightly).
- Push a `v*` tag → CI build → GitHub Release + Visual Studio Marketplace update.
- No Personal Access Token anywhere (global PATs are decommissioned 2026-12-01).
  Authentication is a Microsoft Entra app registration with a GitHub OIDC
  federated credential; the pipeline exchanges tokens at run time and stores
  nothing but the (non-secret) client and tenant IDs.

Two CLIs do all the setup: `az` (logged in) and `gh` (logged in).

---

## 1. Make the VSIX marketplace-legal

In `source.extension.vsixmanifest`:

- **`Identity Id` must match `[A-Za-z0-9-]`, be < 63 chars, start alphanumeric.**
  New listings are validated on upload; old ones (dots, underscores) were
  grandfathered. Bad: `VSNeo_Extension.ec21d06f-…`. Good: `VSNeo-ec21d06f-…`.
  Visual Studio identifies extensions by this ID — changing it means
  uninstalling the old one locally.
- Set `DisplayName`, `Description`, `Tags`, `Publisher` (must equal your
  marketplace publisher ID).
- `<License>LICENSE.txt</License>` and `<Icon>icon.png</Icon>` +
  `<PreviewImage>icon.png</PreviewImage>`. The files must ship inside the VSIX:

```xml
<Content Include="..\LICENSE.txt">
  <Link>LICENSE.txt</Link>
  <IncludeInVSIX>true</IncludeInVSIX>
</Content>
<Content Include="icon.png">
  <IncludeInVSIX>true</IncludeInVSIX>
</Content>
```

Icon: 90×90 PNG at 96 DPI (128×128 variant for the marketplace page).

Note the VSIX ID — it is the public gallery feed URL too:
`https://www.vsixgallery.com/extension/<Id>`.

## 2. Entra: app registration + federated credential (one time, via `az`)

```bash
# 2.1 App + service principal
APP_ID=$(az ad app create --display-name "<repo>-marketplace-publish" --query appId -o tsv)
az ad sp create --id "$APP_ID"

# 2.2 Federated credential for the GitHub "release" environment.
# IMPORTANT: GitHub's OIDC subject includes NUMERIC account/repo IDs:
#   repo:<owner>@<ownerId>/<repo>@<repoId>:environment:release
# Get them:
gh api repos/<owner>/<repo> --jq '.owner.id, .id'

az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-release",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<owner>@<ownerId>/<repo>@<repoId>:environment:release",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

If you guess the subject without the numeric IDs, the first run fails with
`AADSTS700213` and the run log prints the exact subject GitHub presented —
copy it from there.

## 3. Authorize the service principal on the marketplace publisher

The SP needs an Azure DevOps/marketplace identity, then publisher membership.

```bash
# 3.1 Temporary client secret (deleted right after)
SECRET=$(az ad app credential reset --id "$APP_ID" --append \
  --display-name temp-profile-query --years 1 --query password -o tsv)

# 3.2 Token for the Azure DevOps resource, then the SP's profile id
TOKEN=$(curl -s -X POST "https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token" \
  -d "client_id=$APP_ID" -d "client_secret=$SECRET" \
  -d "scope=499b84ac-1321-427f-aa17-267ca6975798/.default" \
  -d "grant_type=client_credentials" | python -c "import json,sys; print(json.load(sys.stdin)['access_token'])")

curl -s -H "Authorization: Bearer $TOKEN" \
  "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1"
# -> the "id" field is the marketplace identity of the SP

# 3.3 Delete every temporary credential afterwards
az ad app credential list --id "$APP_ID" --query "[].keyId" -o tsv | \
  xargs -I{} az ad app credential delete --id "$APP_ID" --key-id {}
```

3.4 In the portal: <https://marketplace.visualstudio.com/manage> → your
publisher → **Members** → **Add** → paste the profile `id` (not an email) →
role **Contributor**.

The app registration now holds **zero secrets** — only the federated
credential.

## 4. GitHub: environment + secrets (via `gh`)

```bash
gh api repos/<owner>/<repo>/environments/release -X PUT
gh secret set AZURE_CLIENT_ID --body "$APP_ID"
gh secret set AZURE_TENANT_ID --body "<tenantId>"
```

Optionally add a required reviewer to the `release` environment in repo
settings — tags then wait for a human approval before touching the
marketplace.

## 5. The publish manifest: `vs-publish.json`

VsixPublisher requires it. With a `.vsix` payload, `identity` may contain
**only** `internalName` (everything else is read from the vsixmanifest), and
categories must come from the VS IDE set (`coding`, `other`, `testing`,
`themes`, … — **not** `productivity`):

```json
{
  "$schema": "http://json.schemastore.org/vsix-publish",
  "categories": [ "coding", "other" ],
  "identity": { "internalName": "<ListingName>" },
  "overview": "README.md",
  "priceCategory": "free",
  "publisher": "<publisherId>",
  "qna": true,
  "repo": "https://github.com/<owner>/<repo>"
}
```

The listing URL becomes
`https://marketplace.visualstudio.com/items?itemName=<publisherId>.<ListingName>`.

## 6. The workflow

Two jobs. `build` needs `contents: write` (the repo default GITHUB_TOKEN is
read-only and the Release step 403s otherwise). `publish` is separate so the
OIDC permission and environment scope to releases only.

```yaml
name: Build
on:
  push:
    branches: [ master ]
    tags: [ 'v*' ]
  pull_request:
    branches: [ master ]
  workflow_dispatch:

jobs:
  build:
    runs-on: windows-latest
    permissions:
      contents: write
    steps:
      - uses: actions/checkout@v6
      - uses: microsoft/setup-msbuild@v3

      - name: Compute version
        id: ver
        shell: pwsh
        run: |
          if ("${{ github.ref_type }}" -eq "tag") {
            $v = "${{ github.ref_name }}".TrimStart('v')
          } else {
            $v = "1.1.${{ github.run_number }}"
          }
          echo "version=$v" >> $env:GITHUB_OUTPUT

      - name: Stamp version
        uses: madskristensen/vsix-version-stamp@v2
        with:
          manifest-file: <Project>/source.extension.vsixmanifest
          version-number: ${{ steps.ver.outputs.version }}

      - name: Restore
        run: msbuild <Project>\<Project>.csproj /t:Restore
      - name: Build
        run: msbuild <Project>\<Project>.csproj /p:Configuration=Release

      - uses: actions/upload-artifact@v4
        with:
          name: <Project>.vsix
          path: <Project>/bin/Release/net472/<Project>.vsix

      - name: Publish to Open VSIX Gallery
        if: github.event_name == 'push' && github.ref == 'refs/heads/master'
        uses: madskristensen/publish-vsixgallery@v1
        with:
          vsix-file: <Project>/bin/Release/net472/<Project>.vsix

      - name: Publish GitHub Release
        if: github.ref_type == 'tag'
        uses: softprops/action-gh-release@v2
        with:
          files: <Project>/bin/Release/net472/<Project>.vsix

  publish:
    if: github.ref_type == 'tag'
    needs: build
    runs-on: windows-latest
    environment: release
    permissions:
      id-token: write
      contents: read
    steps:
      - uses: actions/checkout@v6
      - uses: actions/download-artifact@v4
        with:
          name: <Project>.vsix

      - name: Azure login (OIDC)
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          allow-no-subscriptions: true

      # The generic gallery REST endpoint refuses VS IDE extensions
      # (VsIdeExtensionNotSupported); VsixPublisher owns that protocol, and its
      # -personalAccessToken accepts an Entra token. 499b84ac-... is the Azure
      # DevOps resource the marketplace lives behind.
      - name: Acquire Entra token
        id: entra
        shell: pwsh
        run: |
          $token = az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv
          if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($token)) { throw 'could not acquire Entra token' }
          echo "::add-mask::$token"
          echo "token=$token" >> $env:GITHUB_OUTPUT

      - name: Publish to Visual Studio Marketplace
        uses: madskristensen/publish-marketplace@v2
        with:
          extension-file: <Project>.vsix
          publish-manifest-file: vs-publish.json
          personal-access-code: ${{ steps.entra.outputs.token }}
```

## 7. Release

```bash
git tag v1.0.0 && git push origin v1.0.0
```

~3 minutes later: GitHub Release with the VSIX, marketplace listing updated.
If a run fails before publishing and you need to redo it, move the tag to the
fix commit — a rerun reuses the workflow **as of that tag**:

```bash
git tag -d v1.0.0 && git tag v1.0.0 HEAD && git push origin :refs/tags/v1.0.0 && git push origin v1.0.0
```

## 8. Verify locally before burning a CI run (optional but recommended)

VsixPublisher.exe is at
`C:\Program Files\Microsoft Visual Studio\2022\<edition>\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe`.

Create a temp SP secret (step 3.1), get a token (3.2), then:

```cmd
VsixPublisher.exe login -personalAccessToken <entra-token> -publisherName <publisherId>
VsixPublisher.exe publish -payload test.vsix -publishManifest vs-publish.json -personalAccessToken <entra-token>
```

Tip: publish a **0.0.1** build first (patch `extension.vsixmanifest`'s
`Identity Version` inside the VSIX zip) so the real release version stays the
first proper one — versions must strictly increase. The listing is public the
moment it validates, so do the test when you're fine with the page existing.

A `System.Memory FileLoadException` stack trace from VsixPublisher on exit is
its telemetry crashing *after* a successful upload — noise.

## 9. Errors this setup already hit, and what they mean

| Error | Meaning / fix |
| --- | --- |
| `Resource not accessible by integration` (403) on GitHub Release | build job lacks `permissions: contents: write` |
| `AADSTS700213: No matching federated identity record` | federated credential subject ≠ what GitHub presented; the log prints the exact subject (note the numeric `@ownerId/@repoId`) — copy it into the credential |
| `Extension ID '…' is invalid` (400) | manifest `Identity Id` fails `[A-Za-z0-9-]`, <63 chars |
| `VsIdeExtensionNotSupported` (500) | you called the generic gallery REST endpoint; VS IDE extensions must go through VsixPublisher |
| `VssRequestContentTypeNotSupportedException` | gallery REST without the right api-version (`7.2-preview.2`) — moot once on VsixPublisher |
| `unsupported category: productivity` | not a VS IDE category; use `coding`, `other`, … |
| `VsixPub0017 … cannot contain any identity information other than "InternalName"` | vs-publish.json identity trimmed to `internalName` only |
| `Version number must increase` | re-publishing an existing version; bump the tag/version |
