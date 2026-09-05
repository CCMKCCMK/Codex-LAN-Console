# Third-Party Notices

This file records known third-party software used by or redistributed with the
private preview. The project’s proprietary LICENSE does not replace or limit
the rights granted by these third-party licenses.

## Marked 17.0.5

- Component: Marked JavaScript Markdown parser
- Location: `frontend/web/marked.umd.js`
- Copyright: Copyright (c) 2018+, MarkedJS; Copyright (c) 2011-2018,
  Christopher Jeffrey
- License: MIT, with upstream Markdown attribution
- Source: <https://github.com/markedjs/marked/tree/v17.0.5>
- Included license notice: `LICENSES/MIT-Marked.txt`

The Marked bundle must retain its existing copyright header. Public release
packaging must also preserve any additional attribution included in the exact
upstream version’s license file.

## Gradle Wrapper 9.5.0

- Component: Gradle Wrapper scripts and bootstrap JAR
- Locations: `frontend/android/gradlew`, `frontend/android/gradlew.bat`, and
  `frontend/android/gradle/wrapper/gradle-wrapper.jar`
- Copyright: the original Gradle authors
- License: Apache License 2.0
- Local license text: `LICENSES/Apache-2.0-Gradle.txt`
- Official license text: <https://www.apache.org/licenses/LICENSE-2.0>
- Source: <https://github.com/gradle/gradle>

The wrapper scripts retain their Apache-2.0 headers. Source and binary
distributions must retain the local Apache-2.0 license file and any applicable
upstream NOTICE material.

## Microsoft .NET and ASP.NET Core

The Windows self-contained build redistributes Microsoft .NET 8 and ASP.NET
Core runtime files. Those components are licensed separately by Microsoft and
their contributors, primarily under the MIT License, and may include additional
third-party notices.

- Source: <https://github.com/dotnet/runtime> and
  <https://github.com/dotnet/aspnetcore>
- Licensing information: <https://dotnet.microsoft.com/platform/free>

Every self-contained Windows release must include the exact .NET license and
third-party-notice files corresponding to the runtime version used to build
that release. This repository-level summary is not a substitute for those
version-specific notices.

## Platform SDKs and build services

Android SDK, Apple SDK, Xcode, XcodeGen, GitHub Actions, and their dependencies
are build tools or platform services governed by their own terms. They are not
relicensed by this project. Any component copied into a release artifact must
be included in the release SBOM and must carry its required notices.

XcodeGen is used as an MIT-licensed build-time dependency in the current iOS
workflow. GitHub Actions and Android Gradle Plugin versions must also be recorded
in the build dependency inventory even when they are not shipped in the app.

## Commute maps and elevation

- Leaflet 1.9.4: BSD-2-Clause, vendored under frontend/web/commute/vendor.
  Its LICENSE file is retained beside the code.
- OpenStreetMap contributors: map data under ODbL; attribution is shown in maps.
- CARTO: basemap tiles, attribution retained. Follow provider usage terms.
- Open-Meteo elevation: Copernicus GLO-90 data attribution displayed. The free
  API is non-commercial; arrange appropriate access for commercial deployment.
- UCSD OneBusAway / Wayfinder: remote transit and routing services, not bundled
  datasets. Availability and routing coverage are controlled by the provider.

## Maintenance rule

Before adding, updating, vendoring, or redistributing a dependency:

1. identify its exact version and source;
2. verify that its license is compatible with the project’s current licensing
   plan;
3. preserve copyright, license, and NOTICE files;
4. update this file and the release SBOM; and
5. block the release if licensing information is incomplete.
