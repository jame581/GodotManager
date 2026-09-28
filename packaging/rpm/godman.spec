%global debug_package %{nil}

Name:           godman
Version:        %{_version}
Release:        1%{?dist}
Summary:        A console/TUI tool to install, manage, and activate Godot Engine builds
License:        MIT
URL:            https://github.com/jame581/GodotManager
Source0:        https://github.com/jame581/GodotManager/releases/download/v%{version}/%{name}-linux-x64-%{version}.tar.gz

BuildArch:      x86_64
AutoReqProv:    no

Requires:       glibc
Requires:       libgcc
Requires:       openssl-libs
Requires:       libstdc++
Requires:       libicu
Requires:       zlib

%description
godman is a .NET console/TUI tool to install, manage, and activate
Godot Engine builds (Standard and .NET editions) on Linux and Windows.

Features:
- Install from official URLs or local archives
- Scope-aware installs (user or global)
- Registry of installs with activation via GODOT_HOME and shims
- Interactive TUI and CLI commands
- Dry-run mode for previewing operations

%prep
# The release tarball is flat (tar -czf ... -C publish .), so unpack it into
# a directory of our own.
%setup -q -c

%install
install -Dm 755 %{name} %{buildroot}%{_bindir}/%{name}

%files
%{_bindir}/%{name}

%changelog
