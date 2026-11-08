#!/bin/sh
set -eu
cd "$(dirname "$0")"
facade=/usr/lib/mono/4.5/Facades/netstandard.dll
if [ ! -f "$facade" ]; then
  echo 'Install mono-devel to build the backend.' >&2
  exit 1
fi
mcs -langversion:7.2 -optimize+ -r:"$facade" -r:System.Core -r:System.Xml.Linq -r:Newtonsoft.Json.dll -r:PcmLibrary.dll -out:wrongway-pcm.exe source/LinuxBackend.cs
