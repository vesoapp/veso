#!/bin/bash

# restart.sh - veso server restart script
# Part of the veso project (https://github.com/vesotv)
#
# This script restarts the veso daemon on Linux when using
# the Restart button on the admin dashboard. It supports the
# systemctl, service, and traditional /etc/init.d (sysv) restart
# methods, chosen automatically by which one is found first (in
# that order).
#
# This script is used by the Debian/Ubuntu/Fedora/CentOS packages.

get_service_command() {
    for command in systemctl service; do
        if which $command &>/dev/null; then
            echo $command && return
        fi
    done
    echo "sysv"
}

cmd="$( get_service_command )"
echo "Detected service control platform '$cmd'; using it to restart veso..."
case $cmd in
    'systemctl')
        echo "sleep 2; /usr/bin/sudo $( which systemctl ) restart veso" | at now 
        ;;
    'service')
        echo "sleep 2; /usr/bin/sudo $( which service ) veso restart" | at now 
        ;;
    'sysv')
        echo "sleep 2; /usr/bin/sudo /etc/init.d/veso restart" | at now 
        ;;
esac
exit 0
