using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AtkBattery {
    // Pack trust is deliberately independent of application-update trust.
    internal static class DevicePackTrust {
        internal const int MaxPackBytes=256*1024,MaxMetadataBytes=4096;
        internal const string Prefix="MyFinderDevicePack/v1\n";
        internal const string Mirror="https://myfinder-feedback-relay.wushuai0802.deno.net/device-pack/v1/";
        internal const string GitHub="https://github.com/wushuai0802/myfinder-device-battery/releases/download/device-pack/";
        internal static Dictionary<string,string> Keys(){return new Dictionary<string,string>(StringComparer.Ordinal) {
            {"pack-2026a", "<RSAKeyValue><Modulus>7mcjKe1a7qVOIM/OX0636A8qDJVXVdYPcdbJX0WsEWUFh9aZM4DkTh7IOPVlhuXQx2qqJJaWyJqAcSsVk/UTS+cKqogNVW2Pt0IP3M25fbdm7JSJo4c5HvELfzMwVDjLpF0J9mWWVKtbSl/9eXvQBUnvQiv3XV+Fy0lhIxsTSpwKbU3nD2/7IdpmYzKTTiqs9Q36WyAoppbCK2tHNfC5UCwY3sW/YzTghxpNyhQ5mXrpvBYYoe2Aw2HguBwJi+Usb9oALLm7O2R7PXMrDSQG6PdncrJc1MH20p19JCbd/N9A3mEWbiwd6uJ9be8pscN5zoxIrDSvFvwxBGrcLR0reqBMTyzF898HnF7ju1q/qlx8sYqcfUsGRsa07lD8CfVgDLgISP9eiXCgPi/CXpRgSHbnkUg31zGSBwIvd3fZTkmQ4t1EBPsNGalACoBJpHkXTgE2ZSMwPsIaqDG/4jQfR7agtg547X6OFAm3+IQU5/MjHiF0c84bsIC+6SzC79QZ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>"}
        };}
        internal static byte[] SignedBytes(byte[] raw){
            if(raw==null||raw.Length==0||raw.Length>MaxPackBytes)throw new InvalidDataException("pack_size");
            byte[] prefix=Encoding.ASCII.GetBytes(Prefix),bytes=new byte[prefix.Length+raw.Length];
            Buffer.BlockCopy(prefix,0,bytes,0,prefix.Length);Buffer.BlockCopy(raw,0,bytes,prefix.Length,raw.Length);return bytes;
        }
        internal static string Hash(byte[] raw){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(raw)).Replace("-","").ToLowerInvariant();}
        internal static void Verify(byte[] raw,byte[] signature,IDictionary<string,string> keys){
            byte[] signed=SignedBytes(raw);
            var data=PackJson.Object(PackJson.Parse(raw));object id;string key;
            if(!data.TryGetValue("keyId",out id)||!(id is string)||!keys.TryGetValue((string)id,out key))throw new InvalidDataException("pack_unknown_key");
            if(signature==null||signature.Length==0||signature.Length>MaxMetadataBytes)throw new InvalidDataException("pack_signature_size");
            string text=new UTF8Encoding(false,true).GetString(signature);
            if(!Regex.IsMatch(text,@"\A[A-Za-z0-9+/]+={0,2}(?:\r?\n)?\z",RegexOptions.CultureInvariant))throw new InvalidDataException("pack_signature_format");
            byte[] bytes;try{bytes=Convert.FromBase64String(text);}catch(FormatException){throw new InvalidDataException("pack_signature_format");}
            using(var rsa=new RSACryptoServiceProvider()){
                rsa.PersistKeyInCsp=false;rsa.FromXmlString(key);
                if(!rsa.VerifyData(signed,CryptoConfig.MapNameToOID("SHA256"),bytes))throw new InvalidDataException("pack_signature_invalid");
            }
        }
    }
    // Used by the manual signer. There is no production key override.
    public static class DevicePackSigning {
        public static void Verify(byte[] raw,byte[] signature){DevicePackTrust.Verify(raw,signature,DevicePackTrust.Keys());}
        public static byte[] SignedBytes(byte[] raw){return DevicePackTrust.SignedBytes(raw);}
    }
}
