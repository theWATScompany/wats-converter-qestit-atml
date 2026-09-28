# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0.3] - 2026-09-25

### Fixed
- Numeric values and limits are parsed as standard XML numbers independent of the host regional settings; the `cultureInfo` parameter is now only a fallback for legacy culture-formatted files. Previously the default `da-DK` setting misread dot-decimal values, and limits ignored the setting and used the host culture.
- Limit pairs with equal lower and upper bounds keep both bounds instead of losing the upper limit.
- Unparseable values or limits now fail the conversion with a clear error instead of silently producing an unlimited (LOG) test.

### Added
- Optional QRM `testSocketIndex` is mapped to the WATS test socket index.
- Offline regression tests for number formats, host cultures, comparators, equal bounds and socket index.

## [1.0.0] - 2026-05-26

### Added
- Initial public release of WATS Converter - Qestit QRM ATML.