VERSION := $(shell sed -ne '/^Version:/s/.*  *//p'                      \
                   deployment/fedora-package-x64/pkg-src/veso.spec)

deployment/fedora-package-x64/pkg-src/veso-web-$(VERSION).tar.gz:
	curl -f -L -o deployment/fedora-package-x64/pkg-src/veso-web-$(VERSION).tar.gz \
         https://github.com/veso/veso-web/archive/v$(VERSION).tar.gz \
	|| curl -f -L -o deployment/fedora-package-x64/pkg-src/veso-web-$(VERSION).tar.gz \
         https://github.com/veso/veso-web/archive/master.tar.gz \

srpm: deployment/fedora-package-x64/pkg-src/veso-web-$(VERSION).tar.gz
	cd deployment/fedora-package-x64;                                             \
    SOURCE_DIR=../..                                                              \
    WORKDIR="$${PWD}";                                                            \
    package_temporary_dir="$${WORKDIR}/pkg-dist-tmp";                             \
    pkg_src_dir="$${WORKDIR}/pkg-src";                                            \
    GNU_TAR=1;                                                                    \
    tar                                                                           \
    --transform "s,^\.,veso-$(VERSION),"                                      \
    --exclude='.git*'                                                             \
    --exclude='**/.git'                                                           \
    --exclude='**/.hg'                                                            \
    --exclude='**/.vs'                                                            \
    --exclude='**/.vscode'                                                        \
    --exclude='deployment'                                                        \
    --exclude='**/bin'                                                            \
    --exclude='**/obj'                                                            \
    --exclude='**/.nuget'                                                         \
    --exclude='*.deb'                                                             \
    --exclude='*.rpm'                                                             \
    -czf "pkg-src/veso-$(VERSION).tar.gz"                                     \
    -C $${SOURCE_DIR} ./ || GNU_TAR=0;                                            \
    if [ $${GNU_TAR} -eq 0 ]; then                                                \
        package_temporary_dir="$$(mktemp -d)";                                    \
        mkdir -p "$${package_temporary_dir}/veso";                            \
        tar                                                                       \
        --exclude='.git*'                                                         \
        --exclude='**/.git'                                                       \
        --exclude='**/.hg'                                                        \
        --exclude='**/.vs'                                                        \
        --exclude='**/.vscode'                                                    \
        --exclude='deployment'                                                    \
        --exclude='**/bin'                                                        \
        --exclude='**/obj'                                                        \
        --exclude='**/.nuget'                                                     \
        --exclude='*.deb'                                                         \
        --exclude='*.rpm'                                                         \
        -czf "$${package_temporary_dir}/veso/veso-$(VERSION).tar.gz"      \
        -C $${SOURCE_DIR} ./;                                                     \
        mkdir -p "$${package_temporary_dir}/veso-$(VERSION)";                 \
        tar -xzf "$${package_temporary_dir}/veso/veso-$(VERSION).tar.gz"  \
            -C "$${package_temporary_dir}/veso-$(VERSION);                    \
        rm -f "$${package_temporary_dir}/veso/veso-$(VERSION).tar.gz";    \
        tar -czf "$${SOURCE_DIR}/SOURCES/pkg-src/veso-$(VERSION).tar.gz"      \
            -C "$${package_temporary_dir}" "veso-$(VERSION);                  \
        rm -rf $${package_temporary_dir};                                         \
	fi;                                                                           \
	rpmbuild -bs pkg-src/veso.spec                                            \
	         --define "_sourcedir $$PWD/pkg-src/"                                 \
	         --define "_srcrpmdir $(outdir)"
