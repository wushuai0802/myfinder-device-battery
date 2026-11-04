using System;
using System.Globalization;
using System.IO;
using AtkBattery;

// Compiled only by the publishing tool, with the unmodified client validators.
internal static class PublishCheck {
    static byte[] Read(string path,int limit){
        using(var stream=File.OpenRead(path)){
            if(stream.Length<1||stream.Length>limit)throw new InvalidDataException("publish_file_size");
            using(var memory=new MemoryStream()){stream.CopyTo(memory);return memory.ToArray();}
        }
    }
    static int Main(string[] args){
        try{
            if(args[0]=="sources"){
                Console.WriteLine(DevicePackTrust.GitHub);Console.WriteLine(DevicePackTrust.Mirror);return 0;
            }
            if(args[0]=="validate"){
                DevicePackValidation.Validate(Read(args[1],DevicePackTrust.MaxPackBytes));return 0;
            }
            var pointer=DevicePackPointer.Parse(Read(args[1],DevicePackTrust.MaxMetadataBytes));
            if(args[0]=="verify"){
                byte[] raw=Read(args[2],DevicePackTrust.MaxPackBytes);
                if(raw.Length!=pointer.Size||DevicePackTrust.Hash(raw)!=pointer.Hash)throw new InvalidDataException("publish_digest_mismatch");
                DevicePackTrust.Verify(raw,Read(args[3],DevicePackTrust.MaxMetadataBytes),DevicePackTrust.Keys());
                if(DevicePackValidation.Validate(raw).Version!=pointer.Version)throw new InvalidDataException("publish_version_mismatch");
            }else if(args[0]!="pointer")throw new InvalidDataException("publish_operation");
            Console.WriteLine(pointer.Version.ToString(CultureInfo.InvariantCulture));return 0;
        }catch(Exception){Console.Error.WriteLine("Client device-pack validation failed.");return 1;}
    }
}
