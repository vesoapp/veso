#!/bin/bash

# Builds the RPM inside the Docker container

set -o errexit
set -o xtrace

# Move to source directory
pushd ${SOURCE_DIR}

# Build RPM
make -f .copr/Makefile srpm outdir=/root/rpmbuild/SRPMS
rpmbuild -rb /root/rpmbuild/SRPMS/veso-*.src.rpm

# Move the artifacts out
mkdir -p ${ARTIFACT_DIR}/rpm
mv /root/rpmbuild/RPMS/x86_64/veso-*.rpm /root/rpmbuild/SRPMS/veso-*.src.rpm ${ARTIFACT_DIR}/rpm/
chown -Rc $(stat -c %u:%g ${ARTIFACT_DIR}) ${ARTIFACT_DIR}
