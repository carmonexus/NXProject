using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace NXProject.Services
{
    internal static class ProtectedLogoProvider
    {
        private const string ResourceName = "NXProject.Assets.logo-nexus-xdata.enc";
        private const string ExpectedPlainHash = "5a5547078b825b51fb95e41503f95a3eaea9725f02715fcf18e213fd6c8b13ce";
        private static readonly byte[] Key = System.Text.Encoding.UTF8.GetBytes("NXProjectCommunityLogoKey-2026!");

        private static BitmapImage? _cachedLogo;

        public static BitmapImage GetLogoImage()
        {
            if (_cachedLogo != null)
                return _cachedLogo;

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("O recurso protegido do logo nao foi encontrado.");

            using var encrypted = new MemoryStream();
            stream.CopyTo(encrypted);
            var decryptedBytes = Decrypt(encrypted.ToArray());
            ValidateHash(decryptedBytes);

            using var imageStream = new MemoryStream(decryptedBytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = imageStream;
            image.EndInit();
            image.Freeze();

            _cachedLogo = image;
            return image;
        }

        private static byte[] Decrypt(byte[] encryptedBytes)
        {
            return encryptedBytes
                .Select((value, index) => (byte)(value ^ Key[index % Key.Length]))
                .ToArray();
        }

        private static void ValidateHash(byte[] bytes)
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, ExpectedPlainHash, StringComparison.Ordinal))
                throw new InvalidOperationException("O logo protegido da Community falhou na validacao de integridade.");
        }
    }
}
