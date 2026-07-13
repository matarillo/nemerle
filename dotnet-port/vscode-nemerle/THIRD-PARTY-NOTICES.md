# Third-party notices

This extension package (VSIX) contains, in addition to its own code:

1. Node.js runtime dependencies under `node_modules/` (the language client
   stack). Each of these ships its own `LICENSE` file inside the VSIX at
   `node_modules/<package>/LICENSE*`; see the table below.
2. The bundled .NET language server under `server/`: first-party Nemerle
   assemblies plus redistributed NuGet package assemblies, listed below with
   their licenses. License metadata was read from each package's `.nuspec`
   (and bundled `LICENSE` file where the nuspec uses `type="file"`) on
   2026-07-14.

## First-party (this repository)

`server/Nemerle.LanguageServer.dll`, `server/Nemerle.Compiler.Utils.dll`,
`server/Nemerle.ProjectInfo.dll`, and the compiler assemblies
`server/Nemerle.dll`, `server/Nemerle.Compiler.dll`, `server/Nemerle.Macros.dll`
(and `server/Nemerle.CoreEmit.dll` if present) are built from this repository
and are licensed under the BSD 3-Clause license:

Copyright (c) 2003-2008 The University of Wroclaw. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. The name of the University may not be used to endorse or promote products
   derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE UNIVERSITY ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO
EVENT SHALL THE UNIVERSITY BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS;
OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR
OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

See the repository top-level `COPYRIGHT` file for the authoritative text.

## Bundled .NET assemblies (`server/`)

| Package (assembly base name) | Version | License | Copyright / Authors |
|---|---:|---|---|
| MediatR | 8.1.0 | Apache-2.0 | Copyright Jimmy Bogard |
| Microsoft.Bcl.AsyncInterfaces | 7.0.0 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.Configuration | 6.0.1 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.Configuration.Abstractions | 6.0.0 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.Configuration.Binder | 6.0.0 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.DependencyInjection | 6.0.1 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.DependencyInjection.Abstractions | 6.0.0 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.Logging | 6.0.0 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.Logging.Abstractions | 6.0.0 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.Options | 6.0.0 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.Options.ConfigurationExtensions | 6.0.0 | MIT | (c) Microsoft Corporation |
| Microsoft.Extensions.Primitives | 6.0.0 | MIT | (c) Microsoft Corporation |
| Microsoft.VisualStudio.Threading | 17.6.40 | MIT | (c) Microsoft Corporation |
| Microsoft.VisualStudio.Validation | 17.6.11 | MIT | (c) Microsoft Corporation |
| Microsoft.Win32.Registry | 5.0.0 | MIT | (c) Microsoft Corporation |
| Nerdbank.Streams | 2.10.69 | MIT | (c) Andrew Arnott |
| Newtonsoft.Json | 13.0.3 | MIT | Copyright (c) James Newton-King 2008 |
| OmniSharp.Extensions.JsonRpc | 0.19.9 | MIT (bundled LICENSE file) | LICENSE: (c) .NET Foundation and Contributors; nuspec: Copyright OmniSharp and contributors (c) 2018, David Driscoll |
| OmniSharp.Extensions.LanguageProtocol | 0.19.9 | MIT (bundled LICENSE file) | same as OmniSharp.Extensions.JsonRpc |
| OmniSharp.Extensions.LanguageServer | 0.19.9 | MIT (bundled LICENSE file) | same as OmniSharp.Extensions.JsonRpc |
| OmniSharp.Extensions.LanguageServer.Shared | 0.19.9 | MIT (bundled LICENSE file) | same as OmniSharp.Extensions.JsonRpc |
| System.CodeDom | 10.0.9 | MIT | (c) Microsoft Corporation |
| System.Collections.Immutable | 5.0.0 | MIT | (c) Microsoft Corporation |
| System.Diagnostics.DiagnosticSource | 6.0.0 | MIT | (c) Microsoft Corporation |
| System.IO.Pipelines | 7.0.0 | MIT | (c) Microsoft Corporation |
| System.Reactive | 6.0.0 | MIT | Copyright (c) .NET Foundation and Contributors |
| System.Runtime.CompilerServices.Unsafe | 6.0.0 | MIT | (c) Microsoft Corporation |
| System.Security.AccessControl | 5.0.0 | MIT | (c) Microsoft Corporation |
| System.Security.Principal.Windows | 5.0.0 | MIT | (c) Microsoft Corporation |
| System.Threading.Channels | 6.0.0 | MIT | (c) Microsoft Corporation |

Satellite resource assemblies (`server/<culture>/*.resources.dll`) and
runtime-specific assets (`server/runtimes/**`) belong to the packages above
with the same base name.

## Node.js runtime dependencies (`node_modules/`)

| Package | Version | License |
|---|---:|---|
| vscode-languageclient | 10.1.0 | MIT |
| vscode-languageserver-protocol | 3.18.2 | MIT |
| vscode-jsonrpc | 9.0.1 | MIT |
| vscode-languageserver-types | 3.18.0 | MIT |
| vscode-languageserver-textdocument | 1.0.13 | MIT |
| minimatch | 10.2.5 | BlueOak-1.0.0 |
| brace-expansion | 5.0.7 | MIT |
| balanced-match | 4.0.4 | MIT |
| semver | 7.8.5 | ISC |

The full license texts for these packages are included in the VSIX under
`node_modules/<package>/LICENSE*`.

## MIT License (applies to the MIT-licensed assemblies above)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Apache License 2.0 (applies to MediatR)

The full license text is available at <https://www.apache.org/licenses/LICENSE-2.0>
and at <https://licenses.nuget.org/Apache-2.0> (the license expression declared
by the MediatR 8.1.0 package). Licensed under the Apache License, Version 2.0
(the "License"); you may not use the covered files except in compliance with
the License. Unless required by applicable law or agreed to in writing,
software distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See
the License for the specific language governing permissions and limitations
under the License.
