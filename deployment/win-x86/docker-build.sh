#!/bin/bash

# Builds the ZIP archive inside the Docker container

set -o errexit
set -o xtrace

# Version variables
NSSM_VERSION="nssm-2.24-101-g897c7ad"
NSSM_URL="http://files.evilt.win/nssm/${NSSM_VERSION}.zip"
FFMPEG_VERSION="ffmpeg-4.2.1-win32-static"
FFMPEG_URL="https://ffmpeg.zeranoe.com/builds/win32/static/${FFMPEG_VERSION}.zip"

# Move to source directory
pushd ${SOURCE_DIR}

# Clone down and build Web frontend
web_build_dir="$( mktemp -d )"
web_target="${SOURCE_DIR}/MediaBrowser.WebDashboard/veso-web"
git clone https://github.com/veso/veso-web.git ${web_build_dir}/
pushd ${web_build_dir}
if [[ -n ${web_branch} ]]; then
    checkout -b origin/${web_branch}
fi
yarn install
mkdir -p ${web_target}
mv dist/* ${web_target}/
popd
rm -rf ${web_build_dir}

# Get version
version="$( grep "version:" ./build.yaml | sed -E 's/version: "([0-9\.]+.*)"/\1/' )"

# Build binary
dotnet publish Jellyfin.Server --configuration Release --self-contained --runtime win-x86 --output /dist/veso_${version}/ "-p:GenerateDocumentationFile=false;DebugSymbols=false;DebugType=none;UseAppHost=true"

# Prepare addins
addin_build_dir="$( mktemp -d )"
wget ${NSSM_URL} -O ${addin_build_dir}/nssm.zip
wget ${FFMPEG_URL} -O ${addin_build_dir}/ffmpeg.zip
unzip ${addin_build_dir}/nssm.zip -d ${addin_build_dir}
cp ${addin_build_dir}/${NSSM_VERSION}/win64/nssm.exe /dist/veso_${version}/nssm.exe
unzip ${addin_build_dir}/ffmpeg.zip -d ${addin_build_dir}
cp ${addin_build_dir}/${FFMPEG_VERSION}/bin/ffmpeg.exe /dist/veso_${version}/ffmpeg.exe
cp ${addin_build_dir}/${FFMPEG_VERSION}/bin/ffprobe.exe /dist/veso_${version}/ffprobe.exe
rm -rf ${addin_build_dir}

# Prepare scripts
cp ${SOURCE_DIR}/deployment/windows/legacy/install-veso.ps1 /dist/veso_${version}/install-veso.ps1
cp ${SOURCE_DIR}/deployment/windows/legacy/install.bat /dist/veso_${version}/install.bat

# Create zip package
pushd /dist
zip -r /veso_${version}.portable.zip veso_${version}
popd
rm -rf /dist/veso_${version}

# Move the artifacts out
mkdir -p ${ARTIFACT_DIR}/
mv /veso[-_]*.zip ${ARTIFACT_DIR}/
chown -Rc $(stat -c %u:%g ${ARTIFACT_DIR}) ${ARTIFACT_DIR}
