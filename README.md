# Veso

Veso is an open source media server. An emby/jellyfin fork that will move to a js react framework and focus on remote/rclone mounted media rather than local storage.

## Installation

Docker

```bash
docker run -d \
 --volume /path/to/config:/config \
 --volume /path/to/cache:/cache \
 --volume /path/to/media:/media \
 --user 1000:1000 \
 --p 8096:8096 \
 --p 8920:8920 `#optional` \
 --restart=unless-stopped \
 vesotv/veso
```
Docker compose
```bash
 version: "3"
 services:
   veso:
     image: vesotv/veso
     user: 1000:1000
     ports:
       - 8096:8096
       - 8920:8920
     volumes:
       - /path/to/config:/config
       - /path/to/cache:/cache
       - /path/to/media:/media
```

## Usage

```python
http://localhost:8096
```

## Contributing
Pull requests are welcome. For major changes, please open an issue first to discuss what you would like to change.

Please make sure to update tests as appropriate.

## Contact
``#veso`` on freenode