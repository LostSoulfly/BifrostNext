﻿using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using System.IO;
using System.Text;

namespace BifrostNext.BifrostLSF.KeyExchanges
{
    public class EcdhKeyExchange : IKeyExchange
    {
        public const ushort Identifier = 0;
        public AsymmetricCipherKeyPair ECDHEPair;
        public ECDHBasicAgreement KeyAgreement = new ECDHBasicAgreement();
        public Logger Log = LogManager.GetCurrentClassLogger();
        public string HumanName { get { return "ECDH"; } }
        public ushort KeyExchangeIdentifier { get { return Identifier; } }

        public byte[] FinalizeKeyExchange(byte[] peer_pk)
        {
            PemReader pem = new PemReader(new StringReader(Encoding.UTF8.GetString(peer_pk)));

            ECPublicKeyParameters peer_ecdh_pk = (ECPublicKeyParameters)pem.ReadObject();
            ECPrivateKeyParameters self_priv = ECDHEPair.Private as ECPrivateKeyParameters;

            IBasicAgreement agreement = AgreementUtilities.GetBasicAgreement("ECDH");
            agreement.Init(self_priv);

            return agreement.CalculateAgreement(peer_ecdh_pk).ToByteArray();
        }

        public byte[] GetPublicKey()
        {
            StringWriter sw = new StringWriter();
            PemWriter pem = new PemWriter(sw);

            pem.WriteObject(ECDHEPair.Public);
            pem.Writer.Flush();

            return Encoding.ASCII.GetBytes(sw.ToString());
        }

        public void Initialize()
        {
            X9ECParameters ec_parameters = NistNamedCurves.GetByName("P-521");
            ECDomainParameters ec_specs = new ECDomainParameters(ec_parameters.Curve, ec_parameters.G, ec_parameters.N, ec_parameters.H, ec_parameters.GetSeed());
            ECKeyPairGenerator generator = new ECKeyPairGenerator();
            generator.Init(new ECKeyGenerationParameters(ec_specs, new SecureRandom()));
            Log.Debug("Initialized EC key generator with curve P-521");

            ECDHEPair = generator.GenerateKeyPair();
        }
    }
}