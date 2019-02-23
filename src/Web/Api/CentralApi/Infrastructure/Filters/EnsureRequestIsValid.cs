﻿namespace CentralApi.Infrastructure.Filters
{
    using System;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using BankSystem.Common.Utils;
    using Data;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Mvc.Filters;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Configuration;
    using Models;
    using Newtonsoft.Json;

    public class EnsureRequestIsValid : ActionFilterAttribute
    {
        private const string Id = "model";
        private const string AuthenticationScheme = "bsw";

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var request = context.HttpContext.Request;
            // check the scheme and auth header
            if (!request.Headers.ContainsKey("Authorization") && !request.Headers["Authorization"][0]
                    .Contains(AuthenticationScheme, StringComparison.Ordinal))
            {
                context.Result = new ForbidResult();
                return;
            }

            var authHeader = request.Headers.GetCommaSeparatedValues("Authorization");
            if (authHeader != null)
            {
                var bankName = authHeader[0].Remove(0, AuthenticationScheme.Length).Trim();
                var bankSwiftCode = authHeader[1];
                var encryptedKey = authHeader[2];
                var encryptedIV = authHeader[3];
                var incomingBase64Signature = authHeader[4];

                var isValid = this.IsValidRequest(context, bankName, bankSwiftCode, encryptedKey, encryptedIV, incomingBase64Signature).GetAwaiter().GetResult();

                if (!isValid)
                {
                    context.Result = new ForbidResult();
                    return;
                }
            }

            base.OnActionExecuting(context);
        }

        private async Task<bool> IsValidRequest(ActionExecutingContext context, string bankName, string bankSwiftCode, string encryptedKey,
            string encryptedIV,
            string incomingBase64Signature)
        {
            var request = context.HttpContext.Request;
            var dbContext = request.HttpContext.RequestServices.GetService(typeof(CentralApiDbContext)) as CentralApiDbContext;
            var configuration = request.HttpContext.RequestServices.GetService(typeof(IConfiguration)) as IConfiguration;

            var actionArguments = context.ActionArguments;
            if (actionArguments.ContainsKey(Id))
            {
                var model = actionArguments[Id] as ReceiveTransactionModel;

                var bankApiKey = await dbContext.Banks
                    .Where(b => string.Equals(b.Name, bankName, StringComparison.CurrentCultureIgnoreCase) &&
                                string.Equals(b.SwiftCode, bankSwiftCode, StringComparison.CurrentCultureIgnoreCase))
                    .Select(b => b.ApiKey)
                    .SingleOrDefaultAsync();

                if (bankApiKey == null)
                {
                    return false;
                }

                var serializedModel = JsonConvert.SerializeObject(model);
                var signature = Encoding.UTF8.GetBytes(serializedModel);

                // Decrypt
                string decrypted;
                using (var rsa = RSA.Create())
                {
                    RsaExtensions.FromXmlString(rsa, configuration.GetSection("CentralApiConfiguration:Key").Value);
                    var decryptedKey = rsa.Decrypt(Convert.FromBase64String(encryptedKey), RSAEncryptionPadding.Pkcs1);
                    var decryptedIV = rsa.Decrypt(Convert.FromBase64String(encryptedIV), RSAEncryptionPadding.Pkcs1);

                    decrypted = CryptographyExtensions.Decrypt(Convert.FromBase64String(incomingBase64Signature), decryptedKey, decryptedIV);
                }

                // Verify signature with bank api key
                using (var rsa = RSA.Create())
                {
                    RsaExtensions.FromXmlString(rsa, bankApiKey);
                    var decrypt = Convert.FromBase64String(decrypted);
                    var isVerified = rsa.VerifyData(signature, decrypt, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    return isVerified;
                }
            }

            return false;
        }
    }
}