// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

using SafeWinHttpHandle = Interop.WinHttp.SafeWinHttpHandle;

namespace System.Net.Http
{
    /// <summary>
    /// Static class containing the WinHttp global callback and associated routines.
    /// </summary>
    internal static class WinHttpRequestCallback
    {
        public static Interop.WinHttp.WINHTTP_STATUS_CALLBACK StaticCallbackDelegate =
            new Interop.WinHttp.WINHTTP_STATUS_CALLBACK(WinHttpCallback);

        public static void WinHttpCallback(
            IntPtr handle,
            IntPtr context,
            uint internetStatus,
            IntPtr statusInformation,
            uint statusInformationLength)
        {
            bool invokeCallback = false;

            if (Environment.HasShutdownStarted)
            {
                return;
            }

            if (context == IntPtr.Zero)
            {
                return;
            }

            WinHttpRequestState state = WinHttpRequestState.FromIntPtr(context);
            if (state != null && 
                state.RequestHandle != null &&
                !state.RequestHandle.IsInvalid &&
                state.RequestHandle.DangerousGetHandle() == handle)
            {
                invokeCallback = true;
            }

            WinHttpTraceHelper.TraceCallbackStatus("WinHttpCallback", handle, invokeCallback, internetStatus);

            if (invokeCallback)
            {
                RequestCallback(handle, state, internetStatus, statusInformation, statusInformationLength);
            }
        }
        
        private static void RequestCallback(
            IntPtr handle,
            WinHttpRequestState state,
            uint internetStatus,
            IntPtr statusInformation,
            uint statusInformationLength)
        {
            try
            {
                switch (internetStatus)
                {
                    case Interop.WinHttp.WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE:
                        OnRequestSendRequestComplete(state);
                        return;
                        
                    case Interop.WinHttp.WINHTTP_CALLBACK_STATUS_READ_COMPLETE:
                        OnRequestReadComplete(state, statusInformationLength);
                        return;
                        
                    case Interop.WinHttp.WINHTTP_CALLBACK_STATUS_WRITE_COMPLETE:
                        OnRequestWriteComplete(state);
                        return;
                        
                    case Interop.WinHttp.WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE:
                        OnRequestReceiveResponseHeadersComplete(state);
                        return;

                    case Interop.WinHttp.WINHTTP_CALLBACK_STATUS_REDIRECT:
                        string redirectUriString = Marshal.PtrToStringUni(statusInformation);
                        var redirectUri = new Uri(redirectUriString);
                        OnRequestRedirect(state, redirectUri);
                        return;

                    case Interop.WinHttp.WINHTTP_CALLBACK_STATUS_SENDING_REQUEST:
                        OnRequestSendingRequest(state);
                        return;

                    case Interop.WinHttp.WINHTTP_CALLBACK_STATUS_REQUEST_ERROR:
                        Debug.Assert(
                            statusInformationLength == Marshal.SizeOf<Interop.WinHttp.WINHTTP_ASYNC_RESULT>(),
                            "RequestCallback: statusInformationLength=" + statusInformationLength +
                            " must be sizeof(WINHTTP_ASYNC_RESULT)=" + Marshal.SizeOf<Interop.WinHttp.WINHTTP_ASYNC_RESULT>());

                        var asyncResult = Marshal.PtrToStructure<Interop.WinHttp.WINHTTP_ASYNC_RESULT>(statusInformation);
                        OnRequestError(state, asyncResult);
                        return;

                    default:
                        return;
                }
            }
            catch (Exception ex)
            {
                Interop.WinHttp.WinHttpCloseHandle(handle);
                state.SavedException = ex;
            }
        }

        private static void OnRequestSendRequestComplete(WinHttpRequestState state)
        {
            Debug.Assert(state != null, "OnRequestSendRequestComplete: state is null");
            Debug.Assert(state.TcsSendRequest != null, "OnRequestSendRequestComplete: TcsSendRequest is null");
            Debug.Assert(!state.TcsSendRequest.Task.IsCompleted, "OnRequestSendRequestComplete: TcsSendRequest.Task is completed");
            
            state.TcsSendRequest.TrySetResult(true);
        }

        private static void OnRequestReadComplete(WinHttpRequestState state, uint bytesRead)
        {
            Debug.Assert(state != null, "OnRequestReadComplete: state is null");
            Debug.Assert(state.TcsReadFromResponseStream != null, "TcsReadFromResponseStream is null");
            Debug.Assert(!state.TcsReadFromResponseStream.Task.IsCompleted, "TcsReadFromResponseStream.Task is completed");
            
            state.TcsReadFromResponseStream.TrySetResult((int)bytesRead);
        }

        private static void OnRequestWriteComplete(WinHttpRequestState state)
        {
            Debug.Assert(state != null, "OnRequestWriteComplete: state is null");
            Debug.Assert(state.TcsInternalWriteDataToRequestStream != null, "TcsInternalWriteDataToRequestStream is null");
            Debug.Assert(!state.TcsInternalWriteDataToRequestStream.Task.IsCompleted, "TcsInternalWriteDataToRequestStream.Task is completed");
            
            state.TcsInternalWriteDataToRequestStream.TrySetResult(true);
        }

        private static void OnRequestReceiveResponseHeadersComplete(WinHttpRequestState state)
        {
            Debug.Assert(state != null, "OnRequestReceiveResponseHeadersComplete: state is null");
            Debug.Assert(state.TcsReceiveResponseHeaders != null, "TcsReceiveResponseHeaders is null");
            Debug.Assert(!state.TcsReceiveResponseHeaders.Task.IsCompleted, "TcsReceiveResponseHeaders.Task is completed");

            state.TcsReceiveResponseHeaders.TrySetResult(true);
        }

        private static void OnRequestRedirect(WinHttpRequestState state, Uri redirectUri)
        {
            const string EmptyCookieHeader = "Cookie:";

            Debug.Assert(state != null, "OnRequestRedirect: state is null");
            Debug.Assert(redirectUri != null, "OnRequestRedirect: redirectUri is null");
            Debug.Assert(state.TcsReceiveResponseHeaders != null, "TcsReceiveResponseHeaders is null");
            Debug.Assert(!state.TcsReceiveResponseHeaders.Task.IsCompleted, "TcsReceiveResponseHeaders.Task is completed");

            // If we're manually handling cookies, we need to reset them based on the new URI.
            if (state.Handler.CookieUsePolicy == CookieUsePolicy.UseSpecifiedCookieContainer)
            {
                // Clear cookies.
                if (!Interop.WinHttp.WinHttpAddRequestHeaders(
                    state.RequestHandle,
                    EmptyCookieHeader,
                    (uint)EmptyCookieHeader.Length,
                    Interop.WinHttp.WINHTTP_ADDREQ_FLAG_REPLACE))
                {
                    int lastError = Marshal.GetLastWin32Error();
                    if (lastError != Interop.WinHttp.ERROR_WINHTTP_HEADER_NOT_FOUND)
                    {
                        throw WinHttpException.CreateExceptionUsingError(lastError);
                    }
                }

                // Re-add cookies. The GetCookieHeader() method will return the correct set of
                // cookies based on the redirectUri.
                string cookieHeader = WinHttpHandler.GetCookieHeader(redirectUri, state.Handler.CookieContainer);
                if (!string.IsNullOrEmpty(cookieHeader))
                {
                    if (!Interop.WinHttp.WinHttpAddRequestHeaders(
                        state.RequestHandle,
                        cookieHeader,
                        (uint)cookieHeader.Length,
                        Interop.WinHttp.WINHTTP_ADDREQ_FLAG_ADD))
                    {
                        WinHttpException.ThrowExceptionUsingLastError();
                    }
                }
            }

            state.RequestMessage.RequestUri = redirectUri;
            
            // Redirection to a new uri may require a new connection through a potentially different proxy.
            // If so, we will need to respond to additional 407 proxy auth demands and re-attach any
            // proxy credentials. The ProcessResponse() method looks at the state.LastStatusCode
            // before attaching proxy credentials and marking the HTTP request to be re-submitted.
            // So we need to reset the LastStatusCode remembered. Otherwise, it will see additional 407
            // responses as an indication that proxy auth failed and won't retry the HTTP request.
            if (state.LastStatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            {
                state.LastStatusCode = 0;
            }

            // For security reasons, we drop the server credential if it is a 
            // NetworkCredential.  But we allow credentials in a CredentialCache
            // since they are specifically tied to URI's.
            if (!(state.ServerCredentials is CredentialCache))
            {
                state.ServerCredentials = null;
            }
        }
        
        private static void OnRequestSendingRequest(WinHttpRequestState state)
        {
            Debug.Assert(state != null, "OnRequestSendingRequest: state is null");
            
            if (state.RequestMessage.RequestUri.Scheme != UriScheme.Https)
            {
                // Not SSL/TLS.
                return;
            }

            // Grab the channel binding token (CBT) information from the request handle and put it into
            // the TransportContext object.
            state.TransportContext.SetChannelBinding(state.RequestHandle);

            if (state.ServerCertificateValidationCallback != null)
            {
                IntPtr certHandle = IntPtr.Zero;
                uint certHandleSize = (uint)IntPtr.Size;

                if (!Interop.WinHttp.WinHttpQueryOption(
                    state.RequestHandle,
                    Interop.WinHttp.WINHTTP_OPTION_SERVER_CERT_CONTEXT,
                    ref certHandle,
                    ref certHandleSize))
                {
                    int lastError = Marshal.GetLastWin32Error();
                    throw WinHttpException.CreateExceptionUsingError(lastError);
                }
                
                // Create a managed wrapper around the certificate handle. Since this results in duplicating
                // the handle, we will close the original handle after creating the wrapper.
                var serverCertificate = new X509Certificate2(certHandle);
                Interop.Crypt32.CertFreeCertificateContext(certHandle);

                X509Chain chain = null;
                SslPolicyErrors sslPolicyErrors;

                try
                {
                    WinHttpCertificateHelper.BuildChain(
                        serverCertificate,
                        state.RequestMessage.RequestUri.Host,
                        state.CheckCertificateRevocationList,
                        out chain,
                        out sslPolicyErrors);

                    bool result = state.ServerCertificateValidationCallback(
                        state.RequestMessage,
                        serverCertificate,
                        chain,
                        sslPolicyErrors);
                    if (!result)
                    {
                        throw WinHttpException.CreateExceptionUsingError(
                            (int)Interop.WinHttp.ERROR_WINHTTP_SECURE_FAILURE);
                    }
                }
                finally
                {
                    if (chain != null)
                    {
                        chain.Dispose();
                    }
                }
            }
        }

        private static void OnRequestError(WinHttpRequestState state, Interop.WinHttp.WINHTTP_ASYNC_RESULT asyncResult)
        {
            WinHttpTraceHelper.TraceAsyncError("OnRequestError", asyncResult);
            
            Debug.Assert(state != null, "OnRequestError: state is null");

            var innerException = WinHttpException.CreateExceptionUsingError((int)asyncResult.dwError);

            switch ((uint)asyncResult.dwResult.ToInt32())
            {
                case Interop.WinHttp.API_SEND_REQUEST:
                    state.TcsSendRequest.TrySetException(innerException);
                    break;
                    
                case Interop.WinHttp.API_RECEIVE_RESPONSE:
                    if (asyncResult.dwError == Interop.WinHttp.ERROR_WINHTTP_RESEND_REQUEST)
                    {
                        state.RetryRequest = true;
                        state.TcsReceiveResponseHeaders.TrySetResult(false);
                    }
                    else if (asyncResult.dwError == Interop.WinHttp.ERROR_WINHTTP_CLIENT_AUTH_CERT_NEEDED)
                    {
                        // WinHttp will automatically drop any client SSL certificates that we
                        // have pre-set into the request handle including the NULL certificate
                        // (which means we have no certs to send). For security reasons, we don't
                        // allow the certificate to be re-applied. But we need to tell WinHttp
                        // explicitly that we don't have any certificate to send.
                        WinHttpHandler.SetNoClientCertificate(state.RequestHandle);
                        state.RetryRequest = true;
                        state.TcsReceiveResponseHeaders.TrySetResult(false);
                    }
                    else if (asyncResult.dwError == Interop.WinHttp.ERROR_WINHTTP_OPERATION_CANCELLED)
                    {
                        state.TcsReceiveResponseHeaders.TrySetCanceled(state.CancellationToken);
                    }
                    else
                    {
                        state.TcsReceiveResponseHeaders.TrySetException(innerException);
                    }
                    break;

                case Interop.WinHttp.API_READ_DATA:
                    if (asyncResult.dwError == Interop.WinHttp.ERROR_WINHTTP_OPERATION_CANCELLED)
                    {
                        // TODO: Issue #2165. We need to pass in the cancellation token from the
                        // user's ReadAsync() call into the TrySetCanceled().
                        Debug.WriteLine("RequestCallback: API_READ_DATA - ERROR_WINHTTP_OPERATION_CANCELLED");
                        state.TcsReadFromResponseStream.TrySetCanceled();
                    }
                    else
                    {
                        state.TcsReadFromResponseStream.TrySetException(
                            new IOException(SR.net_http_io_read, innerException));
                    }
                    break;

                case Interop.WinHttp.API_WRITE_DATA:
                    if (asyncResult.dwError == Interop.WinHttp.ERROR_WINHTTP_OPERATION_CANCELLED)
                    {
                        // TODO: Issue #2165. We need to pass in the cancellation token from the
                        // user's WriteAsync() call into the TrySetCanceled().
                        Debug.WriteLine("RequestCallback: API_WRITE_DATA - ERROR_WINHTTP_OPERATION_CANCELLED");
                        state.TcsInternalWriteDataToRequestStream.TrySetCanceled();
                    }
                    else
                    {
                        state.TcsInternalWriteDataToRequestStream.TrySetException(
                            new IOException(SR.net_http_io_write, innerException));
                    }
                    break;

                default:
                    Debug.Fail(
                        "OnRequestError: Result (" + asyncResult.dwResult + ") is not expected.",
                        "Error code: " + asyncResult.dwError + " (" + innerException.Message + ")");
                    break;
            }
        }
    }
}
