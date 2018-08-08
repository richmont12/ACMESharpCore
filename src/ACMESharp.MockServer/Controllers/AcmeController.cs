﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using ACMESharp.Crypto;
using ACMESharp.Crypto.JOSE;
using ACMESharp.MockServer.Storage;
using ACMESharp.Protocol;
using ACMESharp.Protocol.Messages;
using ACMESharp.Protocol.Resources;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using PKISharp.SimplePKI;

namespace ACMESharp.MockServer.Controllers
{
// Sample Directory:
// {
//   "Directory": "/directory",
//   "NewNonce": "https://acme-staging-v02.api.letsencrypt.org/acme/new-nonce",
//   "NewAccount": "https://acme-staging-v02.api.letsencrypt.org/acme/new-acct",
//   "NewOrder": "https://acme-staging-v02.api.letsencrypt.org/acme/new-order",
//   "NewAuthz": null,
//   "RevokeCert": "https://acme-staging-v02.api.letsencrypt.org/acme/revoke-cert",
//   "KeyChange": "https://acme-staging-v02.api.letsencrypt.org/acme/key-change",
//   "Meta": {
//     "TermsOfService": "https://letsencrypt.org/documents/LE-SA-v1.2-November-15-2017.pdf",
//     "Website": "https://letsencrypt.org/docs/staging-environment/",
//     "CaaIdentities": [
//       "letsencrypt.org"
//     ],
//     "ExternalAccountRequired": null
//   }
// }

// Sample Order Response:
// Created
// Server: nginx
// Boulder-Requester: 6294712
// Location: https://acme-staging-v02.api.letsencrypt.org/acme/order/6294712/2084859
// Replay-Nonce: FUrj6pGWocJoAUTr85N6ukDW_KliS75MdDcmZllaQgk
// X-Frame-Options: DENY
// Strict-Transport-Security: max-age=604800
// Cache-Control: no-store, no-cache, max-age=0
// Pragma: no-cache
// Date: Fri, 15 Jun 2018 21:06:12 GMT
// Connection: keep-alive
// Content-Type: application/json
// Content-Length: 815
// Expires: Fri, 15 Jun 2018 21:06:12 GMT
// {
//   "status": "pending",
//   "expires": "2018-06-22T21:06:12Z",
//   "identifiers": [
//     {
//       "type": "dns",
//       "value": "8b-15-d9-10-57-1st.integtests.acme2.zyborg.io"
//     },
//     {
//       "type": "dns",
//       "value": "8b-2e-54-44-17-3rd.integtests.acme2.zyborg.io"
//     },
//     {
//       "type": "dns",
//       "value": "9d-d6-29-43-84-2nd.integtests.acme2.zyborg.io"
//     }
//   ],
//   "authorizations": [
//     "https://acme-staging-v02.api.letsencrypt.org/acme/authz/740KRMwcT0UrLXdUKOlgMnfNbzpSQtRaWjbyA1UgIJ4",
//     "https://acme-staging-v02.api.letsencrypt.org/acme/authz/EytmrLH_JI61fDCfUdesq1bcp6nHBT0wDXmmdT4bjzQ",
//     "https://acme-staging-v02.api.letsencrypt.org/acme/authz/ZQaC05HtAxpv5RB2Ik2GvY_Cp-izmSCItRZor3gfcX0"
//   ],
//   "finalize": "https://acme-staging-v02.api.letsencrypt.org/acme/finalize/6294712/2084859"
// }


    [Route(AcmeController.ControllerRoute)]
    [ApiController]
    public class AcmeController : ControllerBase
    {
        public const string ControllerRoute = "acme";

        private static readonly IEnumerable<string> ChallengeTypes = new[] { "dns-01", "http-01" };
        private static readonly IEnumerable<string> ChallengeTypesForWildcard = new[] { "dns-01" };

        IRepository _repo;
        INonceManager _nonceMgr;

        CertificateAuthority _ca;
        string _caCertPem;

        public AcmeController(IRepository repo, INonceManager nonceMgr, CertificateAuthority ca)
        {
            _repo = repo;
            _nonceMgr = nonceMgr;
            _ca = ca;
        }

        [HttpHead("new-nonce")]
        [HttpGet("new-nonce")]
        public ActionResult NewNonce()
        {
            GenerateNonce();
            return NoContent();
        }

        [HttpPost("new-acct")]
        public ActionResult<Account> NewAccount([FromBody]JwsSignedPayload signedPayload)
        {
            var ph = ExtractProtectedHeader(signedPayload);
            var jwkSer = JsonConvert.SerializeObject(ph.Jwk);

            ValidateNonce(ph);

            var requ = ExtractPayload<CreateAccountRequest>(signedPayload);

            // We start by saving an empty acct in order to compute the next ID
            var dbAcct = new DbAccount();
            _repo.SaveAccount(dbAcct);

            // Then compute the acct-specific URL based on the assigned ID
            // Sample Kid: https://acme-staging-v02.api.letsencrypt.org/acme/acct/6484231
            var acctId = dbAcct.Id.ToString();
          //var kid = ComputeRelativeUrl($"acct/{acctId}").ToString();
            var kid = Url.Action(nameof(GetAccount), new { acctId });

            // Then we actually fill out the details            
            dbAcct.Details = new AccountDetails
            {
                Kid = kid,
                Payload = new Account
                {
                    Id = acctId,
                    Key = ph.Jwk,
                    Contact = requ.Contact?.ToArray(),
                    Status = "testing",
                    TermsOfServiceAgreed = true,
                    InitialIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    CreatedAt = DateTime.Now.ToString(),
                }
            };
            dbAcct.Jwk = jwkSer;
            _repo.SaveAccount(dbAcct);

            GenerateNonce();
            Response.Headers.Add(
                    "Location",
                    dbAcct.Details.Kid);

            return dbAcct.Details.Payload;
        }

        // NOTE THIS IS FOR DEBUG ONLY, ACME SPEC DISALLOWS THIS!
        [HttpGet("acct/{acctId}")]
        public ActionResult<Account> GetAccount(string acctId)
        {
            if (!int.TryParse(acctId, out var id))
                return NotFound();

            var acct = _repo.GetAccount(id);
            if (acct == null)
                return NotFound();

            return acct.Details.Payload;
        }

        [HttpPost("new-order")]
        public ActionResult<Order> NewOrder([FromBody]JwsSignedPayload signedPayload)
        {
            var ph = ExtractProtectedHeader(signedPayload);

            ValidateNonce(ph);

            var requ = ExtractPayload<CreateOrderRequest>(signedPayload);

            var acct = _repo.GetAccountByKid(ph.Kid);
            if (acct == null)
                throw new Exception("could not resolve account");
            var acctId = acct.Id.ToString();

            if (requ.Identifiers.Length == 0)
                throw new Exception("at least one identifier is required");
            
            if (requ.Identifiers.Length > 100)
                throw new Exception("too many identifiers");

            if (requ.Identifiers.Count(x => x.Type != "dns") > 0)
                throw new Exception("unsupported identifier type");

            // We start by saving an empty order so we can compute the next ID
            var dbOrder = new DbOrder();
            _repo.SaveOrder(dbOrder);

            var orderId = dbOrder.Id.ToString();
            var orderIds = new { acctId, orderId };
            var orderUrl = Url.Action(nameof(GetOrder), controller: null,
                    values: orderIds, protocol: Request.Scheme);
            var finalizeUrl = Url.Action(nameof(FinalizeOrder), controller: null,
                    values: orderIds, protocol: Request.Scheme);

            var authzs = new List<DbAuthorization>();
            foreach (var dnsId in requ.Identifiers)
            {
                var authzKey = Guid.NewGuid().ToString();
                var chlngs = new List<DbChallenge>();

                var chlngTypes = ChallengeTypes;
                var isWildcard = dnsId.Value.StartsWith("*.");

                if (isWildcard)
                    chlngTypes = ChallengeTypesForWildcard;

                foreach (var chlngType in chlngTypes)
                {
                    var chlngToken = Guid.NewGuid().ToString();

                    // We start by saving an empty challenge so we can compute the next ID
                    var chlng = new DbChallenge
                    {
                        Payload = new Challenge
                        {
                            Token = chlngToken,
                            // We temporarily assign the token to the URL in order
                            // to satisfy the unique constraint on the URL index
                            Url = chlngToken,
                        }
                    };
                    _repo.SaveChallenge(chlng);

                    chlng.Payload = new Challenge
                    {
                        Type = chlngType,
                        Token = chlngToken,
                        Status = "pending",
                        Url = Url.Action(nameof(GetChallenge), controller: null,
                                values: new { authzKey, challengeId = chlng.Id.ToString() },
                                protocol: Request.Scheme),
                    };
                    _repo.SaveChallenge(chlng);
                    chlngs.Add(chlng);
                }

                var dbAuthz = new DbAuthorization
                {
                    OrderId = dbOrder.Id,
                    Url = Url.Action(nameof(GetAuthorization), controller: null,
                            values: new { authzKey }, protocol: Request.Scheme),
                    Payload = new Authorization
                    {
                        Identifier = dnsId,
                        Status = "pending",
                        Expires = DateTime.Now.AddHours(24).ToUniversalTime().ToString(),
                        Challenges = chlngs.Select(x => x.Payload).ToArray(),
                        Wildcard = isWildcard ? (bool?)true : null,
                    }
                };
                _repo.SaveAuthorization(dbAuthz);
                authzs.Add(dbAuthz);

                foreach (var chlng in chlngs)
                {
                    chlng.AuthorizationId = dbAuthz.Id;
                    _repo.SaveChallenge(chlng);
                }
            }

            dbOrder.Url = orderUrl;
            dbOrder.AccountId = acct.Id;
            dbOrder.Details = new OrderDetails
            {
                OrderUrl = orderUrl,
                Payload = new Order
                {
                    Expires = DateTime.Now.AddHours(24).ToUniversalTime().ToString(),
                    NotBefore = null, // requ.NotBefore,
                    NotAfter = null, //requ.NotAfter,
                    Identifiers = requ.Identifiers,
                    Authorizations = authzs.Select(x => x.Url).ToArray(),
                    Finalize = finalizeUrl,
                    Status = "pending",
                    Error = null,
                    Certificate = null,
                }
            };
            _repo.SaveOrder(dbOrder);

            GenerateNonce();

            return Created(orderUrl, dbOrder.Details.Payload);
        }

        [HttpGet("order/{acctId}/{orderId}")]
        public ActionResult<Order> GetOrder(string acctId, string orderId)
        {
            if (!int.TryParse(acctId, out var acctIdNum))
                return NotFound();
            if (!int.TryParse(orderId, out var orderIdNum))
                return NotFound();

            var order = _repo.GetOrderByUrl(Request.GetEncodedUrl());
            if (order == null)
                return NotFound();
            return order.Details.Payload;
        }

        // "finalize": "https://acme-staging-v02.api.letsencrypt.org/acme/finalize/6294712/2084859"
        [HttpPost("finalize/{acctId}/{orderId}")]
        public ActionResult<Order> FinalizeOrder(string acctId, string orderId,
                [FromBody]JwsSignedPayload signedPayload)
        {
            if (!int.TryParse(acctId, out var acctIdNum))
                return NotFound();
            if (!int.TryParse(orderId, out var orderIdNum))
                return NotFound();

            var ph = ExtractProtectedHeader(signedPayload);

            ValidateNonce(ph);

            var acct = _repo.GetAccountByKid(ph.Kid);
            if (acct == null)
                throw new Exception("could not resolve account");

            var dbOrder = _repo.GetOrder(orderIdNum);
            if (dbOrder == null || dbOrder.AccountId != acctIdNum)
                return NotFound();

            if (acct.Id != dbOrder.AccountId)
                throw new Exception("inconsistent state -- "
                        + "Challenge Order does not belong to resolved Account");

            if (dbOrder.Details.Payload.Status != "pending")
                throw new Exception("Order no longer pending");

            var requ = ExtractPayload<FinalizeOrderRequest>(signedPayload);
            var encodedCsr = CryptoHelper.Base64.UrlDecode(requ.Csr);

            var crt = _ca.Sign(PkiEncodingFormat.Der, encodedCsr, PkiHashAlgorithm.Sha256);
            byte[] crtBytes;
            using (var ms = new MemoryStream())
            {
                crt.Save(ms);
                ms.Flush();
                ms.Position = 0;
                crtBytes = ms.ToArray();
            }

            var certKey = Guid.NewGuid().ToString();
            var certPem = Encoding.UTF8.GetString(crt.Export(PkiEncodingFormat.Pem))
                    + ResolveCaCertPem();
            var dbCert = new DbCertificate
            {
                OrderId = dbOrder.Id,
                CertKey = certKey,
                Native = crtBytes,
                Pem = certPem,
            };
            _repo.SaveCertificate(dbCert);

            dbOrder.Details.Payload.Status = "valid";
            dbOrder.Details.Payload.Certificate = Url.Action(nameof(GetCertificate),
                    controller: null, values: new { certKey }, protocol: Request.Scheme);
            _repo.SaveOrder(dbOrder);

            GenerateNonce();

            return dbOrder.Details.Payload;
        }

        [HttpGet("ca-cert")]
        public ActionResult<string> GetCaCertificate()
        {
            return ResolveCaCertPem();
        }

        [HttpGet("cert/{certKey}")]
        public ActionResult<string> GetCertificate(string certKey)
        {
            var dbCert = _repo.GetCertificateByKey(certKey);
            if (dbCert == null)
                return NotFound();
            
            return dbCert.Pem;
        }

        // "https://acme-staging-v02.api.letsencrypt.org/acme/authz/740KRMwcT0UrLXdUKOlgMnfNbzpSQtRaWjbyA1UgIJ4",
        /*
        // OK
        // Server: nginx
        // X-Frame-Options: DENY
        // Strict-Transport-Security: max-age=604800
        // Cache-Control: no-store, no-cache, max-age=0
        // Pragma: no-cache
        // Date: Fri, 15 Jun 2018 21:06:12 GMT
        // Connection: keep-alive
        // Content-Type: application/json
        // Content-Length: 958
        // Expires: Fri, 15 Jun 2018 21:06:12 GMT
        {
        "identifier": {
            "type": "dns",
            "value": "9d-d6-29-43-84-2nd.integtests.acme2.zyborg.io"
        },
        "status": "pending",
        "expires": "2018-06-22T21:06:12Z",
        "challenges": [
            {
            "type": "dns-01",
            "status": "pending",
            "url": "https://acme-staging-v02.api.letsencrypt.org/acme/challenge/740KRMwcT0UrLXdUKOlgMnfNbzpSQtRaWjbyA1UgIJ4/135906810",
            "token": "6IjV5NN3n5Cl9G4L4t8P2sXkMJzHskOcgZACKFvRK4A"
            },
            {
            "type": "tls-alpn-01",
            "status": "pending",
            "url": "https://acme-staging-v02.api.letsencrypt.org/acme/challenge/740KRMwcT0UrLXdUKOlgMnfNbzpSQtRaWjbyA1UgIJ4/135906811",
            "token": "HOMqeKScOHcrsjZjTfsGqBzSUsQKkMdyITGFt5twlyo"
            },
            {
            "type": "http-01",
            "status": "pending",
            "url": "https://acme-staging-v02.api.letsencrypt.org/acme/challenge/740KRMwcT0UrLXdUKOlgMnfNbzpSQtRaWjbyA1UgIJ4/135906812",
            "token": "emCr04u4Nbesfqm-MyrKFd8IhqxaZrnINpyTPtGQnAQ"
            }
        ]
        }
        */
        [HttpGet("authz/{authzKey}")]
        public ActionResult<Authorization> GetAuthorization(string authzKey)
        {
            var authzUrl = Request.GetEncodedUrl();
            var dbAuthz = _repo.GetAuthorizationByUrl(authzUrl);
            if (dbAuthz == null)
                return NotFound();
            
            return dbAuthz.Payload;
        }

        [HttpGet("challenge/{authzKey}/{challengeId}")]
        public ActionResult<Challenge> GetChallenge(string authzKey, string challengeId)
        {
            var chlngUrl = Request.GetEncodedUrl();
            var dbChlng = _repo.GetChallengeByUrl(chlngUrl);
            if (dbChlng == null)
                return NotFound();
            
            return dbChlng.Payload;
        }

        [HttpPost("challenge/{authzKey}/{challengeId}")]
        public ActionResult<Challenge> AnswerChallenge(string authzKey, string challengeId,
                [FromBody]JwsSignedPayload signedPayload)
        {
            var ph = ExtractProtectedHeader(signedPayload);

            ValidateNonce(ph);

            var acct = _repo.GetAccountByKid(ph.Kid);
            if (acct == null)
                throw new Exception("could not resolve account");

            var chlngUrl = Request.GetEncodedUrl();
            var dbChlng = _repo.GetChallengeByUrl(chlngUrl);
            if (dbChlng == null)
                return NotFound();
            var dbAuthz = _repo.GetAuthorization(dbChlng.AuthorizationId);
            if (dbAuthz == null)
                return NotFound();
            var dbOrder = _repo.GetOrder(dbAuthz.OrderId);
            if (dbOrder == null)
                return NotFound();
            
            if (acct.Id != dbOrder.AccountId)
                throw new Exception("inconsistent state -- "
                        + "Challenge Order does not belong to resolved Account");

            if (dbChlng.Payload.Status != "pending")
                throw new Exception("Challenge no longer pending");

            string answer;
            if (dbChlng.Payload.Type == "dns-01")
            {
                // dns-01 Challenge type takes no answer input
                var requ = ExtractPayload<object>(signedPayload);
                answer = "dns-01";
            }
            else if (dbChlng.Payload.Type == "http-01")
            {
                // http-01 Challenge type takes no answer input
                var requ = ExtractPayload<object>(signedPayload);
                answer = "http-01";
            }
            else
            {
                throw new Exception("unsupported Challenge type: " + dbChlng.Payload.Type);
            }

            dbChlng.Payload.Status = "valid";
            dbChlng.Payload.Validated = DateTime.Now.ToUniversalTime().ToString();
            dbChlng.Payload.ValidationRecord = new object[]
            {
                new { iTakeYourWordForIt = answer }
            };
            _repo.SaveChallenge(dbChlng);

            if (dbAuthz.Payload.Status == "pending")
            {
                dbAuthz.Payload.Status = "valid";
                _repo.SaveAuthorization(dbAuthz);
            }

            GenerateNonce();

            return dbChlng.Payload;
        }

        T ExtractPayload<T>(JwsSignedPayload signedPayload)
        {
            var payloadBytes = CryptoHelper.Base64.UrlDecode(signedPayload.Payload);
            var payloadJson = CryptoHelper.Base64.UrlDecodeToString(signedPayload.Payload);
            return JsonConvert.DeserializeObject<T>(payloadJson);
        }

        ProtectedHeader ExtractProtectedHeader(JwsSignedPayload signedPayload)
        {
            var protectedJson = CryptoHelper.Base64.UrlDecodeToString(signedPayload.Protected);
            return JsonConvert.DeserializeObject<ProtectedHeader>(protectedJson);
        }

        Uri ComputeRelativeUrl(string relPath)
        {
            var requPort = Request.Host.Port.HasValue
                ? Request.Host.Port.Value
                : Request.IsHttps ? 443 : 80;
            var requUrl = new UriBuilder(Request.Scheme, Request.Host.Host, requPort,
                    $"/acme/{relPath}").Uri;
            return requUrl;
        }

        void GenerateNonce()
        {
            Response.Headers.Add(
                    Constants.ReplayNonceHeaderName,
                    _nonceMgr.GenerateNonce());
        }

        void ValidateNonce(JwsSignedPayload signedPayload)
        {
            var protectedHeader = ExtractProtectedHeader(signedPayload);
            ValidateNonce(protectedHeader);
        }

        void ValidateNonce(ProtectedHeader protectedHeader)
        {
            if (!_nonceMgr.ValidateNonce(protectedHeader.Nonce))
                throw new Exception("Bad Nonce");
        }
        string ResolveCaCertPem()
        {
            if (_caCertPem == null)
            {
                lock (typeof(AcmeController))
                {
                    if (_caCertPem == null)
                    {
                        var pemBytes = _ca.CaCertificate.Export(PkiEncodingFormat.Pem);
                        _caCertPem = Encoding.UTF8.GetString(pemBytes);
                    }
                }
            }
            return _caCertPem;
        }
    }
}
