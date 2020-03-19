#!/bin/bash

# Builds the TAR archive inside the Docker container

set -o errexit
set -o xtrace

# Move to source directory
pushd ${SOURCE_DIR}

# Clone down and build Web frontend
web_build_dir="$( mktemp -d )"
web_target="${SOURCE_DIR}/MediaBrowser.WebDashboard/veso-web"
git clone https://github.com/vesotv/veso-web.git ${web_build_dir}/
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

# Build archives
dotnet publish Jellyfin.Server --configuration Release --output /dist/veso_${version}/ "-p:GenerateDocumentationFile=false;DebugSymbols=false;DebugType=none"
tar -cvzf /veso_${version}.portable.tar.gz -C /dist veso_${version}
rm -rf /dist/veso_${version}

# Move the artifacts out
mkdir -p ${ARTIFACT_DIR}/
mv /veso[-_]*.tar.gz ${ARTIFACT_DIR}/
chown -Rc $(stat -c %u:%g ${ARTIFACT_DIR}) ${ARTIFACT_DIR}
