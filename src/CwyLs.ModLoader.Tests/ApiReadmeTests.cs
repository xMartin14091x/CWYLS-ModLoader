using System.IO;
using System.Linq;
using System.Reflection;
using CwyLs.ModLoader.Core.Api;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>The core API README documents every error code the <c>/v1</c> API answers with.</summary>
    public sealed class ApiReadmeTests
    {
        private const string ReadmePath = "src/CwyLs.ModLoader.Core/Api/README.md";

        [RepoFileFact(ReadmePath)]
        public void Core_api_readme_documents_every_v1_error_code_including_408_request_timeout()
        {
            var readme = File.ReadAllText(Path.Combine(TestRepo.Root!, ReadmePath.Replace('/', Path.DirectorySeparatorChar)));
            var codes = typeof(ApiErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
            Assert.Contains("request_timeout", codes);
            foreach (var code in codes)
            {
                Assert.True(readme.Contains("| `" + code + "` |"), "the /v1 error table has no row for " + code);
            }

            Assert.Contains("| `request_timeout` | 408 |", readme);       // the /v1 error table
            Assert.Contains("`request_timeout` **408**", readme);         // the Pack Maker "Errors → HTTP" list
            Assert.Contains("**408** `request_timeout`", readme);         // the strict-input deadline rule
        }
    }
}
