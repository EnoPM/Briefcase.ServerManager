# Contributing

All source code, documentation and user-facing text must be written in English.

Keep the graphical manager separate from BriefcaseNative's C++ runtime. The
manager may invoke the public native tools and administration protocol, but it
must not reproduce server injection or update internals without a compatibility
test.

Installation code must preserve these properties:

- only the selected dedicated server directory may be modified;
- the exact path must be resolved before any write;
- archive entries and manifest paths must remain below that directory;
- download and package hashes must be checked before installation;
- an interrupted or failed replacement must restore previous managed files;
- passwords must never be written to logs or command-line arguments.

Run the contract executable, Release build and Native AOT publication before
submitting a change. Do not commit build output or local server credentials.
