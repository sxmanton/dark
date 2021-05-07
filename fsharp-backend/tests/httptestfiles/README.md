# Dark HTTP server tests

The files in this directory are tests of the Http server. A test typically
comprises a request and a response, and may also include one or more handler.

The implementation of the tests is in Tests/BwdServer.Tests.fs.

# Http handlers

An example HTTP handler looks like this

```
[http-handler POST /]
"hello post"
```

The header has the method and path. The body is Dark code using the same F#
syntax we use elsewhere (see tests/testfiles/README.md for a guide)

# Requests

A request is in the following format:

```
[request]
POST / HTTP/1.1
Host: HOST
Date: Sun, 08 Nov 2020 15:38:01 GMT
Content-Type: application/json; charset=utf-8
Content-Length: 22

{ "field1": "value1" }
```

Responses are sent to the server, where presumably they'll hit the HTTP handler
that you've set up (or not, if that's what you're testing)

Note that while HTTP requires headers to end lines with \r\n instead of \n, the
test files use \n (the files are parsed and corrected before being sent to the
server).

# Responses

Responses are the expected response from the server. An example looks like this:

```
[response]
HTTP/1.1 200 OK
Date: xxx, xx xxx xxxx xx:xx:xx xxx
content-type: application/json; charset=utf-8
Access-Control-Allow-Origin: *
x-darklang-execution-id: 0123456789
Server: darklang
Content-Length: LENGTH

{
  "b": { "field1": "value1"},
  "f": null,
  "fb": "{ \"field1\": \"value1\" }",
  "j": { "field1": "value1"}
}
```

The response is expected to be perfect down to the byte. However, JSON
responses are not necessarily identical as the F# server gives slightly
different JSON to the OCaml server. JSON is parsed and compared.

# Adjusting for minor differences

## Json responses

See above

## LENGTH

The token "LENGTH" will be replaced with the length of the request body or the response body.

## HOST

The token "HOST" will be replaced with host the request is being sent to. This
is useful as the tests use different servers for OCaml and F#.

## FSHARPONLY and OCAMLONLY

Lines containing FSHARPONLY are stripped from being sent/compared to the Ocaml request/response, and vice-versa for OCAMLONLY.
