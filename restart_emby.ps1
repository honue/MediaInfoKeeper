$remote = "root@192.168.33.100"
$localDll = "E:/MasterWorkspace/EmbyDev/MediaInfoKeeper/Build/bin/Release/net5.0/MediaInfoKeeper.dll"
$remoteDir = "/opt/emby/config/plugins"

# 1. 拷贝 DLL
scp $localDll "${remote}:${remoteDir}/"

# 2. 远程执行 Docker 命令
ssh $remote "cd /opt/emby && docker compose restart"
