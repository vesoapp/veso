<h1 align="center">Veso</h1>
<h3 align="center">The Free Software Media System</h3>
<h4 align="center">A Jellyfin Fork with Fixes and Usability in Mind</h4>

<p align="center">
<img alt="Logo Banner" src="https://user-images.githubusercontent.com/1161544/173489550-b48543f5-9aa4-43b8-a604-c1ec4ef248ff.svg?sanitize=true"/>
</p>

### docker-compose

```
version: '3.7'
services:
  veso:
    container_name: veso
    environment:
      - TZ=America/New_York
    user: 1000:1000
    volumes:
      - '/dev/dri:/dev/dri'
      - '/path/to/config:/config'
      - '/path/to/cache:/cache'
      - '/path/to/media:/media'
    ports:
      - 8096:8096
      - 8920:8920
    devices:
      - /dev/dri:/dev/dri
    restart: unless-stopped
    image: vesotv/veso:latest

```
Join us on discord https://discord.gg/Ce4PmFcX7Y
