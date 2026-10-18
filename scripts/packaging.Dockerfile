# The packaging image: builds the MSI (wixl), completes it (msibuild), signs (osslsigncode) — on Linux.
# Built by scripts/package.sh on demand; measured with msitools/wixl 0.103 and osslsigncode 2.8.
FROM ubuntu:24.04
RUN apt-get update -qq \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends msitools wixl osslsigncode python3 ca-certificates >/dev/null \
 && rm -rf /var/lib/apt/lists/*
