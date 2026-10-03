# Third-party inputs

The MIT license covers this project's platform code, tools and dedicated Windows DMA
driver. It does not grant rights to upstream core or driver binaries.
These binaries are local dependencies in ignored vendor/ and drivers/ directories.

## NVPermissive core

Distributed upstream by GreenDamTan/RainCandyTech. The initial Linux wrapper
declared MODULE_LICENSE("GPL"); the pinned original archive did not include
a standalone license file. Updated source/license was not supplied with the
user-provided EFI. No additional redistribution rights are inferred.

- New user-provided UEFI v0.2.2, core 469dc0c, built 2026-09-20 17:15:18 UTC:
  - EFI SHA-256: 6d7a035713a4d8f226e5523156d81154ef04dffef21727dcd3ee262917c2e0ed
  - Embedded AMD64 ET_REL object at offset 32256, length 1386248 bytes.
  - Core SHA-256: b533b7b245ed606c151ca336b9a6bacebe935e3ec478246e879c668a4dd98a6a
- Legacy core 380bdf3:
  - Core SHA-256: c9702b4887d397272f86dcc25eea2bb11a46d636c91311d7b71f2fb8b01951e5
  - Original archive SHA-256: d5e89e77e121e1295cb39d331e0d511275b6edc1d7236376e0fd13ac6f0c79c0

Upstream distribution: https://alist.homelabproject.cc/foxipan/vGPU/CMP_90HX/GraphicsUnlock
prepare-core.ps1 requires locally supplied inputs and validates exact hashes.

## Windows driver inputs

prepare-reference-drivers.ps1 obtains binaries from commit
f802245f0f6d318670210f732092bb754396c5c3 of ngthaihoc/CMP30HXmodtoGEN2.
Use -SourceDirectory to supply an existing local copy instead of downloading.

- WinRing0x64.sys SHA-256: 11bd2c9f9e2397c9a16e0990e4ed2cf0679498fe0fd418a3dfdac60b5c160ee5
- ThrottleStop.sys SHA-256: 16f83f056177c4ec24c7e99d01ca9d9d6713bd0497eeedb777a3ffefa99c97f0

ThrottleStop arena IOCTL 0x8000645c maps <u64 physical><u32 bytes> and returns
an 8-byte process-local address; 0x80006460 unmaps that address with zero output.
Driver use and loading are subject to their respective upstream terms.
