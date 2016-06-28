﻿using System;
using System.Text;
using System.IO;
using System.Linq;

using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

using NLog;

namespace Bifrost
{
    public class ServerLink : EncryptedLink
    {
        private Logger Log = LogManager.GetCurrentClassLogger();
        public bool AuthenticateClient { get; set; }

        /// <summary>
        /// Creates a new EncryptedLink object from the perspective of a server.
        /// </summary>
        /// <param name="tunnel">The ITunnel to use.</param>
        /// <param name="auth_client">If auth_client is set to true, the client's RSA public key and key exchange parameters are checked against the certificate authority.</param>
        public ServerLink(ITunnel tunnel, bool auth_client = true)
        {
            Tunnel = tunnel;
            AuthenticateClient = auth_client;
        }

        /// <summary>
        /// Perform a server-side handshake.
        /// </summary>
        /// <returns>true if the handshake was successful, false otherwise.</returns>
        public bool PerformHandshake()
        {
            Message msg = Receive();

            if (msg == null)
            {
                Log.Error("Connection closed?");
                Tunnel.Close();
                return false;
            }

            if (!msg.CheckType(MessageType.AuthRequest, 0x00))
            {
                Log.Error("Received message of type {0}/0x{1:X} while expecting AuthRequest/0x00. Terminating handshake.", msg.Type, msg.Subtype);
                Tunnel.Close();
                return false;
            }

            byte[] rsa_public_key = msg.Store["rsa_public_key"];
            byte[] rsa_signature = msg.Store["rsa_signature"];
            byte[] ecdh_public_key = msg.Store["ecdh_public_key"];
            byte[] ecdh_signature = msg.Store["ecdh_signature"];

            byte[] timestamp = msg.Store["timestamp"];
            DateTime timestamp_dt = MessageHelpers.GetDateTime(BitConverter.ToInt64(timestamp, 0));
            TimeSpan difference = (DateTime.UtcNow - timestamp_dt).Duration();

            if (!timestamp.SequenceEqual(ecdh_public_key.Skip(ecdh_public_key.Length - 8)))
            {
                Log.Error("Timestamp mismatch between ECDH public key and explicit timestamp. Terminating handshake.");
                Tunnel.Close();
                return false;
            }

            if (difference > MaximumTimeMismatch)
            {
                Log.Error("Timestamp difference between client and server exceeds allowed window of {0}(provided timestamp is {1}, our clock is {2}). Terminating handshake.", MaximumTimeMismatch, timestamp_dt, DateTime.UtcNow);
                Tunnel.Close();
                return false;
            }

            Log.Info("Clock drift between peers is {0}.", difference);

            if (!AuthenticateClient && !RsaHelpers.VerifyData(rsa_public_key, rsa_signature, CertificateAuthority))
            {
                Log.Error("Failed to verify RSA public key against certificate authority. Terminating handshake.");
                Tunnel.Close();
                return false;
            }

            var parameters = (RsaKeyParameters)RsaHelpers.PemDeserialize(Encoding.UTF8.GetString(rsa_public_key));

            if (AuthenticateClient && !RsaHelpers.VerifyData(ecdh_public_key, ecdh_signature, parameters))
            {
                Log.Error("Failed to verify ECDH public key authenticity. Terminating handshake.");
                Tunnel.Close();
                return false;
            }

            PemReader pem = new PemReader(new StringReader(Encoding.UTF8.GetString(ecdh_public_key)));

            ECPublicKeyParameters peer_ecdh_pk = (ECPublicKeyParameters)pem.ReadObject();
            ECPrivateKeyParameters self_priv = ECDHEPair.Private as ECPrivateKeyParameters;

            SharedSalt = new byte[16];
            RNG.GetBytes(SharedSalt);

            SendMessage(MessageHelpers.CreateECDHEResponse(this));

            IBasicAgreement agreement = AgreementUtilities.GetBasicAgreement("ECDH");
            agreement.Init(self_priv);

            var shared_secret = agreement.CalculateAgreement(peer_ecdh_pk).ToByteArray();

            byte[] prekey = CalculateHKDF(shared_secret);
            Array.Copy(prekey, 0, SecretKey, 0, 32);
            Array.Copy(prekey, 32, MACKey, 0, 32);

            AES = new GcmBlockCipher(new AesFastEngine());
            AESKey = new KeyParameter(SecretKey);
            HMAC.Key = MACKey;

            CurrentEncryption = EncryptionMode.AES;

            Log.Info("Handshake successful.");
            return true;
        }
    }
}
