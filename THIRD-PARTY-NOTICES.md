# Third-party notices and evidence provenance

The MIT license applies to original Timbre code and documentation. It does not grant rights to third-party software, images, trademarks, or vendor-derived material.

## EPOS components

EPOS Gaming Suite, `EPOSGamingSuiteService`, `EAPO.dll`, the EPOS audio driver, and vendor installers remain third-party components. They are detected or used from a compatible user installation. They are not included in the source commit or portable package.

Local preservation copies are excluded by `.gitignore`. Redistribution permission for those vendor packages has not been established. Installing or bundling them is outside the current portable release.

## Research fixtures

The [fixture inventory](tests/Timbre.Tests/Fixtures/README.md) records what each capture represents and how it was obtained. The [manifest](tests/Timbre.Tests/Fixtures/manifest.json) pins fixture hashes; tests do not regenerate it.

- Binary APO fixtures are captured control-state buffers or documented initializer output, not recordings or redistributed vendor executables.
- B20 topology JSON is sanitized to remove source-machine endpoint/device prefixes while retaining control values and topology relationships.
- `b20-eq-frequencies.png` and `gsx-playback-eq-frequencies.png` are user-supplied EPOS Gaming Suite screenshots retained as frequency-label evidence. Their vendor UI content is outside the project's MIT grant.
- `gsx-sidetone-ui-map.json` contains lookup-table evidence recovered from Gaming Suite PE data. Its source hashes and limitations are documented in the fixture inventory. The MIT grant does not relicense that vendor-derived material.
- Static initializer metadata records observed offsets and values with source provenance; it does not include the inspected executable.

Fixtures are included for reproducibility. Their inclusion does not confer third-party redistribution rights. Any broader redistribution should account for the identified vendor-derived material separately.

## Names

EPOS, Sennheiser, and product names are used to identify the hardware/software under investigation. The project has no stated affiliation with or endorsement from those vendors.
