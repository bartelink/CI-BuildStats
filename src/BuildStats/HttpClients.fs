namespace BuildStats

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks
open FSharp.Control.Tasks
open Logfella
open Microsoft.Net.Http.Headers

exception BrokenCircuitException

type HttpRequestFactory = unit -> HttpRequestMessage

type IResilientHttpClient =
    abstract member SendAsync : HttpRequestFactory -> Task<HttpResponseMessage>

type BaseHttpClient (clientFactory : IHttpClientFactory) =
    interface IResilientHttpClient with
        member __.SendAsync (requestFactory : HttpRequestFactory) =
            let userAgent = sprintf "%s/%s" Env.name Env.appVersion
            let client = clientFactory.CreateClient()
            client.DefaultRequestHeaders.Add("User-Agent", userAgent)
            let request = requestFactory()
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
            client.SendAsync request

type CircuitBreakerHttpClient (httpClient       : IResilientHttpClient,
                               minBreakDuration : TimeSpan) =

    let mutable isBrokenCircuit = false
    let mutable brokenSince     = DateTime.MinValue

    let getBreakDuration (response : HttpResponseMessage) =
        match response.Headers.Contains HeaderNames.RetryAfter with
        | false -> minBreakDuration
        | true  ->
            match response.Headers.RetryAfter.Delta.HasValue with
            | true  -> max minBreakDuration response.Headers.RetryAfter.Delta.Value
            | false -> minBreakDuration

    let isClientError (status : HttpStatusCode) =
        let clientErrorCodes =
            [ 400; 401; 402; 403; 404; 405; 406; 407; 409; 410;
              411; 412; 413; 414; 415; 416; 417; 418; 421; 422;
              423; 424; 426; 428; 431; 444; 451; 499 ]
        clientErrorCodes |> List.contains ((int)status)

    interface IResilientHttpClient with
        member __.SendAsync (requestFactory : HttpRequestFactory) =
            task {
                match isBrokenCircuit with
                | true  ->
                    let brokenDuration = DateTime.Now - brokenSince
                    isBrokenCircuit <- brokenDuration <= minBreakDuration
                    return raise BrokenCircuitException
                | false ->
                    let! response = httpClient.SendAsync requestFactory
                    match response.IsSuccessStatusCode || isClientError response.StatusCode with
                    | true  -> return response
                    | false ->
                        let breakDuration = getBreakDuration response
                        Log.Warning(
                            sprintf
                                "Request to '%s' has failed (HTTP status code: %i). Breaking circuit for: %f sec."
                                (response.RequestMessage.RequestUri.ToString())
                                (int response.StatusCode)
                                breakDuration.TotalSeconds,
                                dict [
                                    "httpClient", "circuitBreakerClient" :> obj
                                ])
                        brokenSince     <- DateTime.Now
                        isBrokenCircuit <- true
                        return response
            }

type RetryHttpClient (httpClient : IResilientHttpClient,
                      maxRetries : int) =

    let getWaitDuration (retryCount : int) =
        TimeSpan.FromSeconds(Math.Pow(2.0, float retryCount))

    let isWorthRetrying (status : HttpStatusCode) =
        status = HttpStatusCode.RequestTimeout
        || status = HttpStatusCode.BadGateway
        || status = HttpStatusCode.ServiceUnavailable
        || status = HttpStatusCode.GatewayTimeout

    let rec sendAsync (requestFactory : HttpRequestFactory) (retryCount : int) : Task<HttpResponseMessage> =
        task {
            let! response = httpClient.SendAsync requestFactory

            match response.IsSuccessStatusCode with
            | true  -> return response
            | false ->
                match retryCount > 0 && isWorthRetrying response.StatusCode with
                | false -> return response
                | true  ->
                    let waitDuration = getWaitDuration retryCount
                    Log.Warning(
                        sprintf
                            "Request to '%s' has failed. The HTTP response status code was: %i. Max retries left: %i. Next wait duration: %f sec."
                                (response.RequestMessage.RequestUri.ToString())
                                (int response.StatusCode)
                                retryCount
                                waitDuration.TotalSeconds,
                        dict [
                            "httpClient", "retryClient" :> obj
                        ])
                    do! Task.Delay waitDuration
                    return! sendAsync requestFactory (retryCount - 1)
        }

    interface IResilientHttpClient with
        member __.SendAsync (requestFactory : HttpRequestFactory) =
            sendAsync requestFactory maxRetries

type FallbackHttpClient (httpClient : IResilientHttpClient) =

    let isClientError (status : HttpStatusCode) =
        let clientErrorCodes =
            [ 400; 401; 402; 403; 404; 405; 406; 407; 409; 410;
              411; 412; 413; 414; 415; 416; 417; 418; 421; 422;
              423; 424; 426; 428; 431; 444; 451; 499 ]
        clientErrorCodes |> List.contains ((int)status)

    member __.SendAsync (requestFactory : HttpRequestFactory) =
        task {
            try
                let! response = httpClient.SendAsync requestFactory
                match response.StatusCode with
                | HttpStatusCode.OK -> return! response.Content.ReadAsStringAsync()
                | _                 ->
                    if isClientError response.StatusCode then
                        Log.Warning(
                            sprintf
                                "Request to '%s' has failed due to a HTTP client error: %i."
                                (response.RequestMessage.RequestUri.ToString())
                                (int response.StatusCode),
                            dict [
                                "httpClient", "fallbackClient" :> obj
                            ])
                    return ""
            with
                | :? BrokenCircuitException -> return ""
                | :? TaskCanceledException  -> return ""
                | ex ->
                    Log.Error(
                        "Exception thrown when trying to send HTTP request.",
                        dict [
                            "httpClient", "fallbackClient" :> obj
                        ],
                        ex)
                    return ""
        }
