using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web;
using System.Web.Caching;
using System.Web.Routing;
using Autofac;
using SmartStore.Core.Configuration;
using SmartStore.Core.Domain.Orders;
using SmartStore.Core.Domain.Payments;
using SmartStore.Core.Localization;
using SmartStore.Core.Logging;
using SmartStore.Services;
using SmartStore.Services.Orders;
using SmartStore.Services.Payments;
using SmartStore.Utilities;
using SmartStore.Web.Framework.Plugins;
using SmartStore.PayPal.PayPalSvc;
using SmartStore.PayPal.Settings;
using System.Globalization;
using SmartStore.PayPal.Services;
using SmartStore.Core.Plugins;

namespace SmartStore.PayPal
{
    public abstract class PayPalProviderBase<TSetting> : PaymentMethodBase, IConfigurable where TSetting : PayPalApiSettingsBase, ISettings, new()
    {
        protected PayPalProviderBase()
		{
			Logger = NullLogger.Instance;
		}

		public TSetting Settings { get; set; }

		public ILogger Logger { get; set; }

		public ICommonServices CommonServices { get; set; }

		public IOrderService OrderService { get; set; }

		public IComponentContext ComponentContext { get; set; }

		protected abstract string GetResourceRootKey();

		private PluginHelper _helper;
		public PluginHelper Helper 
		{
			get
			{
				if (_helper == null)
				{
					_helper = new PluginHelper(this.ComponentContext, "SmartStore.PayPal", GetResourceRootKey());
				}
				return _helper;
			}
		}

        /// <summary>
        /// Verifies IPN
        /// </summary>
        /// <param name="formString">Form string</param>
        /// <param name="values">Values</param>
        /// <returns>Result</returns>
        public bool VerifyIPN(string formString, out Dictionary<string, string> values)
        {
            var req = (HttpWebRequest)WebRequest.Create(PayPalHelper.GetPaypalUrl(Settings));
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.UserAgent = HttpContext.Current.Request.UserAgent;

            string formContent = string.Format("{0}&cmd=_notify-validate", formString);
            req.ContentLength = formContent.Length;

            using (var sw = new StreamWriter(req.GetRequestStream(), Encoding.ASCII))
            {
                sw.Write(formContent);
            }

            string response = null;
            using (var sr = new StreamReader(req.GetResponse().GetResponseStream()))
            {
                response = HttpUtility.UrlDecode(sr.ReadToEnd());
            }
            bool success = response.Trim().Equals("VERIFIED", StringComparison.OrdinalIgnoreCase);

            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string l in formString.Split('&'))
            {
                string line = HttpUtility.UrlDecode(l).Trim();
                int equalPox = line.IndexOf('=');
                if (equalPox >= 0)
                    values.Add(line.Substring(0, equalPox), line.Substring(equalPox + 1));
            }

            return success;
        }

        /// <summary>
        /// Captures payment
        /// </summary>
        /// <param name="capturePaymentRequest">Capture payment request</param>
        /// <returns>Capture payment result</returns>
        public override CapturePaymentResult Capture(CapturePaymentRequest capturePaymentRequest)
        {
            var result = new CapturePaymentResult();

            string authorizationId = capturePaymentRequest.Order.AuthorizationTransactionId;
            var req = new DoCaptureReq();
            req.DoCaptureRequest = new DoCaptureRequestType();
            req.DoCaptureRequest.Version = PayPalHelper.GetApiVersion();
            req.DoCaptureRequest.AuthorizationID = authorizationId;
            req.DoCaptureRequest.Amount = new BasicAmountType();
            req.DoCaptureRequest.Amount.Value = Math.Round(capturePaymentRequest.Order.OrderTotal, 2).ToString("N", new CultureInfo("en-us"));
            req.DoCaptureRequest.Amount.currencyID = (CurrencyCodeType)Enum.Parse(typeof(CurrencyCodeType), Helper.CurrencyCode, true);
            req.DoCaptureRequest.CompleteType = CompleteCodeType.Complete;

            using (var service = new PayPalAPIAASoapBinding())
            {
                service.Url = PayPalHelper.GetPaypalServiceUrl(Settings);

                service.RequesterCredentials = new CustomSecurityHeaderType();
                service.RequesterCredentials.Credentials = new UserIdPasswordType();
                service.RequesterCredentials.Credentials.Username = Settings.ApiAccountName;
                service.RequesterCredentials.Credentials.Password = Settings.ApiAccountPassword;
                service.RequesterCredentials.Credentials.Signature = Settings.Signature;
                service.RequesterCredentials.Credentials.Subject = "";

                DoCaptureResponseType response = service.DoCapture(req);

                string error = "";
                bool success = PayPalHelper.CheckSuccess(_helper, response, out error);
                if (success)
                {
                    result.NewPaymentStatus = PaymentStatus.Paid;
                    result.CaptureTransactionId = response.DoCaptureResponseDetails.PaymentInfo.TransactionID;
                    result.CaptureTransactionResult = response.Ack.ToString();
                }
                else
                {
                    result.AddError(error);
                }
            }
            return result;
        }

        /// <summary>
        /// Handles refund
        /// </summary>
        /// <param name="request">RefundPaymentRequest</param>
        /// <returns>RefundPaymentResult</returns>
        public override RefundPaymentResult Refund(RefundPaymentRequest request)
        {
            var result = new RefundPaymentResult();
            string transactionId = request.Order.CaptureTransactionId;

            var req = new RefundTransactionReq();
            req.RefundTransactionRequest = new RefundTransactionRequestType();
            //NOTE: Specify amount in partial refund
            req.RefundTransactionRequest.RefundType = RefundType.Full;
            req.RefundTransactionRequest.RefundTypeSpecified = true;
            req.RefundTransactionRequest.Version = PayPalHelper.GetApiVersion();
            req.RefundTransactionRequest.TransactionID = transactionId;

            using (var service = new PayPalAPISoapBinding())
            {
                service.Url = PayPalHelper.GetPaypalServiceUrl(Settings);

                service.RequesterCredentials = new CustomSecurityHeaderType();
                service.RequesterCredentials.Credentials = new UserIdPasswordType();
                service.RequesterCredentials.Credentials.Username = Settings.ApiAccountName;
                service.RequesterCredentials.Credentials.Password = Settings.ApiAccountPassword;
                service.RequesterCredentials.Credentials.Signature = Settings.Signature;
                service.RequesterCredentials.Credentials.Subject = "";

                RefundTransactionResponseType response = service.RefundTransaction(req);

                string error = string.Empty;
                bool Success = PayPalHelper.CheckSuccess(_helper, response, out error);
                if (Success)
                {
                    result.NewPaymentStatus = PaymentStatus.Refunded;
                    //cancelPaymentResult.RefundTransactionID = response.RefundTransactionID;
                }
                else
                {
                    result.AddError(error);
                }
            }

            return result;
        }

        /// <summary>
        /// Voids a payment
        /// </summary>
        /// <param name="voidPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public override VoidPaymentResult Void(VoidPaymentRequest request)
        {
            var result = new VoidPaymentResult();

            string transactionId = request.Order.AuthorizationTransactionId;
            if (String.IsNullOrEmpty(transactionId))
                transactionId = request.Order.CaptureTransactionId;

            var req = new DoVoidReq();
            req.DoVoidRequest = new DoVoidRequestType();
            req.DoVoidRequest.Version = PayPalHelper.GetApiVersion();
            req.DoVoidRequest.AuthorizationID = transactionId;


            using (var service = new PayPalAPIAASoapBinding())
            {
                service.Url = PayPalHelper.GetPaypalServiceUrl(Settings);

                service.RequesterCredentials = new CustomSecurityHeaderType();
                service.RequesterCredentials.Credentials = new UserIdPasswordType();
                service.RequesterCredentials.Credentials.Username = Settings.ApiAccountName;
                service.RequesterCredentials.Credentials.Password = Settings.ApiAccountPassword;
                service.RequesterCredentials.Credentials.Signature = Settings.Signature;
                service.RequesterCredentials.Credentials.Subject = "";

                DoVoidResponseType response = service.DoVoid(req);

                string error = "";
                bool success = PayPalHelper.CheckSuccess(_helper, response, out error);
                if (success)
                {
                    result.NewPaymentStatus = PaymentStatus.Voided;
                    //result.VoidTransactionID = response.RefundTransactionID;
                }
                else
                {
                    result.AddError(error);
                }
            }
            return result;
        }

        /// <summary>
        /// Cancels a recurring payment
        /// </summary>
        /// <param name="cancelPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public override CancelRecurringPaymentResult CancelRecurringPayment(CancelRecurringPaymentRequest request)
        {

            var result = new CancelRecurringPaymentResult();
            var order = request.Order;

            var req = new ManageRecurringPaymentsProfileStatusReq();
            req.ManageRecurringPaymentsProfileStatusRequest = new ManageRecurringPaymentsProfileStatusRequestType();
            req.ManageRecurringPaymentsProfileStatusRequest.Version = PayPalHelper.GetApiVersion();
            var details = new ManageRecurringPaymentsProfileStatusRequestDetailsType();
            req.ManageRecurringPaymentsProfileStatusRequest.ManageRecurringPaymentsProfileStatusRequestDetails = details;

            details.Action = StatusChangeActionType.Cancel;
            //Recurring payments profile ID returned in the CreateRecurringPaymentsProfile response
            details.ProfileID = order.SubscriptionTransactionId;

            using (var service = new PayPalAPIAASoapBinding())
            {
                service.Url = PayPalHelper.GetPaypalServiceUrl(Settings);

                service.RequesterCredentials = new CustomSecurityHeaderType();
                service.RequesterCredentials.Credentials = new UserIdPasswordType();
                service.RequesterCredentials.Credentials.Username = Settings.ApiAccountName;
                service.RequesterCredentials.Credentials.Password = Settings.ApiAccountPassword;
                service.RequesterCredentials.Credentials.Signature = Settings.Signature;
                service.RequesterCredentials.Credentials.Subject = "";

                var response = service.ManageRecurringPaymentsProfileStatus(req);

                string error = "";
                if (!PayPalHelper.CheckSuccess(_helper, response, out error))
                {
                    result.AddError(error);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets a route for provider configuration
        /// </summary>
        /// <param name="actionName">Action name</param>
        /// <param name="controllerName">Controller name</param>
        /// <param name="routeValues">Route values</param>
        public override void GetConfigurationRoute(out string actionName, out string controllerName, out RouteValueDictionary routeValues)
        {
            actionName = "Configure";
            controllerName = GetControllerName();
            routeValues = new RouteValueDictionary() { { "area", "SmartStore.PayPal" } };
        }

        /// <summary>
        /// Gets a route for payment info
        /// </summary>
        /// <param name="actionName">Action name</param>
        /// <param name="controllerName">Controller name</param>
        /// <param name="routeValues">Route values</param>
        public override void GetPaymentInfoRoute(out string actionName, out string controllerName, out RouteValueDictionary routeValues)
        {
            actionName = "PaymentInfo";
            controllerName = GetControllerName();
            routeValues = new RouteValueDictionary() { { "area", "SmartStore.PayPal" } };
        }

        protected abstract string GetControllerName();

        public override bool SupportCapture
        {
            get { return true; }
        }

        public override bool SupportPartiallyRefund
        {
            get { return false; }
        }

        public override bool SupportRefund
        {
            get { return true; }
        }

        public override bool SupportVoid
        {
            get { return true; }
        }
    }
}

