﻿using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Runtime.Caching;
using System.Threading.Tasks;
using RedisMaster;
using System.Data.SqlTypes;
using System.Diagnostics.Eventing.Reader;
using System.Diagnostics;

// You can use print statements as follows for debugging, they'll be visible when running tests.
Console.WriteLine("Logs from your program will appear here!");

bool isServerRunning = true;
//Mapping for set and get commands
// Dictionary<string, string> dict = new Dictionary<string, string>();
var db = MemoryCache.Default;
DateTime EPOCH = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
string dir = string.Empty;
string dbFilename = string.Empty;
int port = 0;
string masterPort = string.Empty;
string masterHost = string.Empty;
List<int> slavePort = new List<int>();

int slaveOffset = 0;
bool masterCommand = false;

int countSlaveProcessed = 0;//count of slaves processed for wait command

//Replication variables
string role = "master";
string masterRid = randomStringGenerator(40);
string masterOffset = "0";

string lastStreamId = "0-0"; // last stream id for XADD command
Dictionary<string, string[]> streamKeyDB = new Dictionary<string, string[]>();//for storing the stream key and the stream ids
Dictionary<string, List<string>> streamIdDB = new Dictionary<string, List<string>>();//for storing the stream ids and the key-value pairs
Dictionary<string, string> timeStampDB = new Dictionary<string, string>();//for storing the stream ids and the time stamps

args = Environment.GetCommandLineArgs();
await handleArguements(args);
loadRDBfile();

// Create a TCP/IP server.
TcpListener server = new TcpListener(IPAddress.Any, port == 0 ? 6380 : port);
server.Start();
while (isServerRunning)
{
    var acceptTask = server.AcceptSocketAsync(); // wait for client
    var client = await acceptTask;
    Console.WriteLine($"Client connected at {client.RemoteEndPoint}");
    Task.Run(() => handleClient(client)); // handle client in a separate thread
}
void loadRDBfile()
{
    string filePath = Path.Combine(dir, dbFilename);
    if (!File.Exists(filePath))
    {
        return;
    }
    try
    {
        byte[] data = File.ReadAllBytes(filePath);
        Console.WriteLine($"File read successfully. Data (hex): {BitConverter.ToString(data)}");
        ParseRedisData(data);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error reading file: {ex.Message}");
    }
}
void handleClient(Socket client)
{
    while (true)
    {
        byte[] buffer = new byte[1024];
        int bytesRead = client.Receive(buffer);
        if (bytesRead == 0)
            return; // client disconnected

        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();

        handleCommands(message, client);
    }
}
async Task handleCommands(string message, Socket client)
{
    if (message.StartsWith("MASTER:"))//checks if the commands is sent by master or client
    {
        masterCommand = true;
        message = message.Substring(7);
    }
    var command = message.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    string cmdsize = command[0].Substring(1);

    int argsize = int.Parse(cmdsize); //arguement size which is the first line
    string cmd = command[2].ToUpper();
    string response = "none";
    if (cmd == "SET" && argsize >= 3)//Set's the key to the value
    {
        string key = command[4];
        string val = command[6];
        if (argsize == 5)
        {
            int expt = int.Parse(command[10]);
            if (command[8].ToUpper() == "PX")
                db.Set(key, (object)val, DateTimeOffset.Now.AddMilliseconds(expt));
            else if (command[8].ToUpper() == "EX")
                db.Set(key, (object)val, DateTimeOffset.Now.AddSeconds(expt));
        }
        else
        {
            db.Set(key, (object)val, DateTimeOffset.MaxValue);
        }
        // dict[key] = val;
        response = "+OK\r\n";
        if (role == "master")//Syncing the write command to slave
        {
            Task.Run(() => handleSendingToSlave(message));
        }
        else if (masterCommand)//offset count is updated if the command is sent by master
        {
            slaveOffset += Encoding.UTF8.GetByteCount(message) + 2;
        }
    }
    else if (cmd == "GET" && argsize == 2)//gets the key if it exists
    {
        string key = command[4];
        if (db[key] != null)
        {
            response = $"+{db[key]}\r\n";
        }
        else
        {
            response = "$-1\r\n";
        }
    }
    else if (cmd == "PING")//pings the connection
    {
        response = "+PONG\r\n";
        if (role == "master")//Syncing the write command to slave
        {
            Task.Run(() => handleSendingToSlave(message));
        }
        else if (masterCommand)
        {
            slaveOffset += Encoding.UTF8.GetByteCount(message) + 2;
        }
    }
    else if (cmd == "ECHO" && argsize > 1)//echo's the string
    {
        string echo = "";
        for (int i = 4; i < command.Length; i += 2)
        {
            echo += command[i] + " ";
        }
        response = $"+{echo}\r\n";
    }
    else if (cmd == "CONFIG" && command[4].ToUpper() == "GET" && argsize == 3)//used to get the directories of dir or dbfilename
    {
        if (command[6].Equals("dir"))
        {
            response = $"*2\r\n$3\r\ndir\r\n${dir.Length}\r\n{dir}\r\n";
        }
        else if (command[6].Equals("dbfilename"))
        {
            response = $"*2\r\n$3\r\ndir\r\n${dbFilename.Length}\r\n{dbFilename}\r\n";
        }
    }
    else if (cmd == "KEYS" && argsize == 2)//used to get the keys present in the database
    {
        string pattern = command[4];
        List<string> matchingKeys = new List<string>();

        foreach (var keyValue in db)
        {
            string key = keyValue.Key;
            // Match all keys if pattern is "*"
            if (pattern == "*")
            {
                matchingKeys.Add(key);
            }
            // Match keys starting with a specific prefix (e.g., "f*")
            else if (pattern.EndsWith("*") && key.StartsWith(pattern.TrimEnd('*')))
            {
                matchingKeys.Add(key);
            }
        }
        int count = matchingKeys.Count;
        response = $"*{count}\r\n";
        foreach (var key in matchingKeys)
        {
            response += $"${key.Length}\r\n{key}\r\n";
        }
    }
    else if (cmd == "INFO" && argsize == 2)//gets the replication info of the port/connection
    {
        if (command[4] == "replication")
        {
            string infoRole = $"role:{role}";
            string infoMasterRid = $"master_replid:{masterRid}";
            string infoMasterOffset = $"master_repl_offset:{masterOffset}";
            response = $"*11\r\n" +
                   $"$11\r\nReplication\r\n" +
                   $"${infoRole.Length}\r\n{infoRole}\r\n" +
                   $"$18\r\nconnected_slaves:{slavePort.Count}\r\n" +
                   $"${infoMasterRid.Length}\r\n{infoMasterRid}\r\n" +
                   "$15\r\nmaster_replid2:\r\n" +
                   $"${infoMasterOffset.Length}\r\n{infoMasterOffset}\r\n" +
                   "$20\r\nsecond_repl_offset:0\r\n" +
                   "$21\r\nrepl_backlog_active:0\r\n" +
                   "$19\r\nrepl_backlog_size:0\r\n" +
                   "$32\r\nrepl_backlog_first_byte_offset:0\r\n" +
                   "$22\r\nrepl_backlog_histlen:0\r\n";
        }
        else
            response = $"-ERR applied it yet\r\n";
    }
    else if (cmd == "REPLCONF")//2nd and 3rd handshakes and also used for getting acknowledgement
    {
        response = "+OK\r\n";
        if (argsize >= 3 && command[4].ToUpper() == "GETACK" && role == "master")//master is getting the ACK from slave
        {
            response = $"+Getting acknowledgement from slave\r\n";
            Task.Run(() => handleSendingToSlave(message));
        }
        else if (argsize >= 3 && command[4].ToUpper() == "GETACK")//master is asking for acknowledgment from slave
        {
            string slaveOffsetString = slaveOffset.ToString();
            string ack = $"*3\r\n$8\r\nREPLCONF\r\n$3\r\nACK\r\n${slaveOffsetString.Length}\r\n{slaveOffsetString}\r\n";
            // Connect to the master client on port 6380
            TcpClient mClient = new TcpClient(masterHost, int.Parse(masterPort));
            NetworkStream mStream = mClient.GetStream(); // connected to stream to send and recieve response
            handleSendingToMaster(mStream, ack);//sending the ack to master
        }
        else if (argsize >= 3 && command[4] == "listening-port")
        {
            slavePort.Add(int.Parse(command[6]));
            Console.WriteLine($"Slave connected on port: {int.Parse(command[6])}");
        }
        else if (role == "master" && command[4].ToUpper() == "ACK")
        {
            Console.WriteLine($"Received ACK from slave with offset: {command[6]}");
        }

        if (masterCommand)
        {
            slaveOffset += Encoding.UTF8.GetByteCount(message) + 2;
            Console.WriteLine($"message propagated:{message}");
        }
    }
    else if (cmd == "PSYNC")//Final handshake that fully syncs the slave with master and also sends the empty rdb file
    {
        response = $"+FULLRESYNC {masterRid} {masterOffset}\r\n";
        string rdbContents = "524544495330303131fa0972656469732d76657205372e322e30fa0a72656469732d62697473c040fa056374696d65c26d08bc65fa08757365642d6d656dc2b0c41000fa08616f662d62617365c000fff06e3bfec0ff5aa2";
        response += $"{rdbContents.Length}\r\n{rdbContents}";
    }
    else if (cmd == "WAIT" && role == "master")
    {
        int slaveCount = int.Parse(command[4]);
        int timeout = int.Parse(command[6]);
        while (countSlaveProcessed < slaveCount && timeout > 0)//wait till count of slaves processed is equal to slave count sent by client or the timeout occurs
        {
            await Task.Delay(100); // wait for 100 ms
            timeout -= 100;
        }
        response = $"+{slavePort.Count}\r\n";
    }
    else if (cmd == "TYPE")
    {
        string key = command[4];
        string val = db[key].ToString();
        if (db[key] != null)
        {
            if (int.TryParse(val, out _))
                response = $"+integer\r\n"; //key is a integer type
            else if (bool.TryParse(val, out _))
                response = $"+boolean\r\n";//key is a boolean type
            else
                response = $"+string\r\n";//key is a string type
        }
        else
        {
            response = "$-1\r\n";
        }

        if (streamKeyDB[key] != null)
            response = $"+stream\r\n";//key is a stream type
    }
    else if (cmd == "XADD")//to add stream data types
    {
        string streamKey = command[4];
        string streamId = command[6];
        if (streamId == "*")
            streamId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() + "-*";
        string[] timeSeq = streamId.Split("-");
        if (timeSeq[0] != "*" && timeSeq[1] == "*")
            generateSequenceForStreamId(ref timeSeq);
        if (!checkStreamId(timeSeq))
        {
            response = "-ERR invalid stream id\r\n";
            client.Send(Encoding.UTF8.GetBytes(response));
            Console.WriteLine($"Sent: {response}");
            return;
        }
        streamId = timeSeq[0] + "-" + timeSeq[1];
        string keyValuePairs = string.Join(" ", command.Skip(8)); // all the key-value pairs stored as entries in the stream
        List<string> keyValueList = new List<string>();
        for (int i = 8; i < command.Length; i += 2)
        {
            keyValueList.Add(command[i]);
        }
        if (!streamKeyDB.ContainsKey(streamKey))
        {
            streamKeyDB[streamKey] = new string[] { streamId };
        }
        else
        {
            var existingIds = streamKeyDB[streamKey].ToList();
            existingIds.Add(streamId);
            streamKeyDB[streamKey] = existingIds.ToArray();
        }
        streamIdDB[streamId] = keyValueList; // setting the stream id as key for key-value pairs so that during Type command , it can be identified as stream
        response = $"+{streamId}\r\n";
    }
    else if (cmd == "XRANGE" && argsize >= 2 && streamKeyDB.ContainsKey(command[4]))
    {
        int idCount = argsize - 2;
        response = $"*{idCount}\r\n";
        for (int i = 6; i < command.Length; i += 2)
        {
            string streamid = command[i];
            if (!streamid.Contains("-"))
            {
                streamid = streamid + "-0";
            }
            if (!streamIdDB.ContainsKey(streamid))
                continue;
            response += $"*2\r\n${streamid.Length}\r\n{streamid}\r\n*{streamIdDB[streamid].Count}\r\n";
            foreach (var keyValue in streamIdDB[streamid])
            {
                response += $"${keyValue.Length}\r\n{keyValue}\r\n";
            }
        }
    }
    else
    {
        response = "-ERR unknown command\r\n";
    }
    client.Send(Encoding.UTF8.GetBytes(response));
    Console.WriteLine($"Sent: {response}");
}
async Task handleArguements(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--dir") && i + 1 < args.Length)
        {
            dir = args[i + 1];
        }
        else if (args[i].Equals("--dbfilename") && i + 1 < args.Length)
        {
            dbFilename = args[i + 1];
        }
        else if (args[i].Equals("--port") && i + 1 < args.Length)
        {
            port = int.Parse(args[i + 1]);
        }
        else if (args[i].Equals("--replicaof") && i + 1 < args.Length)
        {
            role = "slave";
            await handleMaster(args[i + 1]);
        }
        // else if (args[i].Equals("--master") && i + 1 < args.Length)
        // {
        //     MasterProgram masterProgram = new MasterProgram();
        //     await masterProgram.Run(args.Skip(i + 1).ToArray());
        // }
    }
}
async Task handleMaster(string str)
{
    string[] master = str.Split(" ");
    if (master.Length == 2)
    {
        masterPort = master[1];
        masterHost = master[0];
        // Connect to the master client on port 6380
        TcpClient mClient = new TcpClient(masterHost, int.Parse(masterPort));
        NetworkStream mStream = mClient.GetStream(); // connected to stream to send and recieve response

        //Handshake 1 by sending PING command
        string pingCommand = $"*1\r\n$4\r\nPING\r\n";
        string message = handleSendingToMaster(mStream, pingCommand);
        //Checking if the response is PONG or not
        if (message != "+PONG")
        {
            Console.WriteLine("Failed to connect to master.");
            return;
        }
        //Handshake 2 by sending REPLCONF listening-port {slave-port} command
        string replconfCommand1 = $"*3\r\n$8\r\nREPLCONF\r\n$14\r\nlistening-port\r\n$4\r\n{port}\r\n";
        message = handleSendingToMaster(mStream, replconfCommand1);
        //Checking if response is OK or not
        if (message != "+OK")
        {
            Console.WriteLine("Failed to send REPLCONF listening-port command.");
            return;
        }
        //Handshake 3 by sending REPLCONF capa psync2 command
        string replconfCommand2 = $"*3\r\n$8\r\nREPLCONF\r\n$4\r\ncapa\r\n$6\r\npsync2\r\n";
        message = handleSendingToMaster(mStream, replconfCommand2);
        //Checking if response is OK or not
        if (message != "+OK")
        {
            Console.WriteLine("Failed to send REPLCONF capa psync2 command.");
            return;
        }
        //Handshake 4 by sending PSYNC command
        string psyncCommand = $"*3\r\n$5\r\nPSYNC\r\n$1\r\n?\r\n$2\r\n-1\r\n";
        message = handleSendingToMaster(mStream, psyncCommand);
        //Checking if response is FULLRESYNC or not
        if (!message.Contains("+FULLRESYNC"))
        {
            Console.WriteLine("Failed to send PSYNC command.");
            return;
        }

        //Final message after connecting
        Console.WriteLine("Slave is connected to the master");
    }
}
string handleSendingToMaster(NetworkStream mStream, string command)
{
    byte[] bytesToSend = Encoding.UTF8.GetBytes(command);
    mStream.Write(bytesToSend, 0, bytesToSend.Length);
    byte[] buffer = new byte[1024];
    int bytesRead = mStream.Read(buffer, 0, buffer.Length);
    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
    Console.WriteLine($"Received from master: {message}");
    return message;
}
void handleSendingToSlave(string message)
{
    for (int i = 0; i < slavePort.Count; i++)
    {
        TcpClient sClient = new TcpClient("127.0.0.1", slavePort[i]);
        NetworkStream sStream = sClient.GetStream(); // connected to stream to send and recieve response
        string masterMessage = $"MASTER:{message}";
        byte[] bytesToSend = Encoding.UTF8.GetBytes(masterMessage);
        sStream.Write(bytesToSend, 0, bytesToSend.Length);
        byte[] buffer = new byte[1024];
        int bytesRead = sStream.Read(buffer, 0, buffer.Length);
        countSlaveProcessed++;
    }

}
void ParseRedisData(byte[] data)
{
    int index = 0;
    try
    {
        while (index < data.Length)
        {
            if (data[index] == 0xFB)
            {
                index = ParseDatabaseSection(data, index);
            }
            else if (data[index] == 0xFF)
                break;
            else
                index++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error parsing data: {ex.Message}");
    }
}
int ParseDatabaseSection(byte[] data, int index)
{
    index = index + 1;
    int length = data[index];
    Console.WriteLine($"Length of key-value pair: {length}");
    index = index + 2;

    for (int i = 0; i < length; i++)
    {
        int expTime = 0;

        //Checking if the key has expiry or not
        if (data[index] == 0xFC || data[index] == 0xFD)
            expTime = ParseExpiryKey(data, ref index);

        //Checking if the key is a string or not
        if (data[index] != 0x00)
        {
            throw new Exception("Data format other than string is not supported yet");
        }
        index++;
        int keyLength = data[index];
        Console.WriteLine($"Key length: {keyLength}");
        index++;
        //Parsing the key
        string key = ParseString(data, ref index, keyLength);
        Console.WriteLine($"Key: {key}");
        int valueLength = data[index];
        Console.WriteLine($"Value length: {valueLength}");
        index++;
        //Parsing the value
        string value = ParseString(data, ref index, valueLength);
        Console.WriteLine($"Value: {value}");
        Console.WriteLine($"Setting key: {key} with value: {value}");

        // Set the key-value pair in the dictionary
        if (expTime > 0)
            db.Set(key, value, DateTimeOffset.Now.AddMilliseconds(expTime));
        else if (expTime == 0)
            db.Set(key, value, DateTimeOffset.MaxValue);
    }
    return index;
}

string ParseString(byte[] data, ref int index, int length)
{
    string result = Encoding.Default.GetString(data.Skip(index).Take(length).ToArray());
    index += length;
    return result;
}
int ParseExpiryKey(byte[] data, ref int index)
{
    index++;
    // Parse the 8-byte unsigned long in little-endian format
    DateTime epoch = EPOCH;
    var tempTime = BitConverter.ToInt64(data, index);
    var dateTime = data[index - 1] == 0xFC ? epoch.AddMilliseconds(tempTime).ToUniversalTime() : epoch.AddSeconds(tempTime).ToUniversalTime();
    index += 8;
    var timeDifference = dateTime - DateTime.UtcNow;
    int expTime = (int)timeDifference.TotalMilliseconds;
    Console.WriteLine($"Expiration time in milliseconds: {expTime}");
    return expTime;
}

string randomStringGenerator(int length)
{
    var chars = "0123456789abcdefghijklmnopqrstuvwxyz";
    var output = new StringBuilder();
    var random = new Random();
    for (int i = 0; i < length; i++)
    {
        output.Append(chars[random.Next(chars.Length)]);
    }
    return output.ToString();
}

bool checkStreamId(string[] timeSeq)//checking if the stream Id is valid or not
{
    if (timeSeq.Length != 2)
        return false;
    string[] lastTimeSeq = lastStreamId.Split("-");
    if (long.Parse(timeSeq[0]) < long.Parse(lastTimeSeq[0]))//if the milliseconds time is less than the last time
        return false;
    else if (long.Parse(timeSeq[0]) == long.Parse(lastTimeSeq[0]) && long.Parse(timeSeq[1]) <= long.Parse(lastTimeSeq[1]))//if the sequence number is less than the last sequence number and milliseconds are equal
        return false;
    lastStreamId = timeSeq[0] + "-" + timeSeq[1];//updating the last stream id
    return true;
}

void generateSequenceForStreamId(ref string[] timeSeq)//generating the sequence number for stream id
{
    if (timeStampDB.ContainsKey(timeSeq[0]))
    {
        int seq = int.Parse(timeStampDB[timeSeq[0]]);
        seq++;
        timeStampDB[timeSeq[0]] = seq.ToString();
    }
    else
    {
        if (timeSeq[0] != "0")
            timeStampDB[timeSeq[0]] = "0";
        else
            timeStampDB[timeSeq[0]] = "1";
    }
    timeSeq[1] = timeStampDB[timeSeq[0]];
}