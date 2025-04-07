# my-own-reddis-Csharp

Note:- Multiple clients can connect with this server simultaneously and can send multiple commands.

1. Ping Command Implemented - It returns PONG
2. Echo command Implemented - If arguement is given with this, the arguement is returned, if not an error is returned.
3. Set & Get command Implemented - it works as mapping just like in redis.
4. Added Expiry functionality in Set command with px keyword (in milliseconds).
5. Started RDB Persistence and Implemented Config Get command to get the dir and dbfilename.
6. Now server can read rdb files and sync key-value pairs and also use KEYS command to fetch keys.
7. Now server can also read keys with expiry time in it in both millisecond and seconds format.
8. Now using EX option in SET command we can set key with expiry time in seconds.
