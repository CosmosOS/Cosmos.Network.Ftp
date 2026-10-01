// This code is licensed under the BSD 3-Clause license (see LICENSE.txt for details)

using NUnit.Framework;

namespace Cosmos.Network.Ftp.Tests;

public class FtpPathTests
{
    public class Resolve
    {
        [TestCase("/", "", ExpectedResult = "/")]
        [TestCase("/", "docs", ExpectedResult = "/docs")]
        [TestCase("/docs", "notes.txt", ExpectedResult = "/docs/notes.txt")]
        [TestCase("/docs", "/music", ExpectedResult = "/music")]
        [TestCase("/docs/a", "..", ExpectedResult = "/docs")]
        [TestCase("/docs", "./a/./b/", ExpectedResult = "/docs/a/b")]
        [TestCase("/docs", "a//b", ExpectedResult = "/docs/a/b")]
        [TestCase("/", "..", ExpectedResult = "/")]
        [TestCase("/docs", "../../../etc/passwd", ExpectedResult = "/etc/passwd")]
        [TestCase("/docs", "/../../etc", ExpectedResult = "/etc")]
        [TestCase("/", "dir one/file two", ExpectedResult = "/dir one/file two")]
        public string WhenResolved_ResultStaysUnderRoot(string workingDirectory, string path) =>
            FtpPath.Resolve(workingDirectory, path);
    }

    public class ToPhysical
    {
        [TestCase("/mnt", "/", ExpectedResult = "/mnt")]
        [TestCase("/mnt/", "/", ExpectedResult = "/mnt")]
        [TestCase("/mnt", "/docs/a.txt", ExpectedResult = "/mnt/docs/a.txt")]
        [TestCase("/", "/", ExpectedResult = "/")]
        [TestCase("/", "/docs", ExpectedResult = "/docs")]
        public string WhenMapped_ResultIsUnderRootDirectory(string rootDirectory, string path) =>
            FtpPath.ToPhysical(rootDirectory, path);
    }

    public class GetName
    {
        [TestCase("/docs/a.txt", ExpectedResult = "a.txt")]
        [TestCase("/docs", ExpectedResult = "docs")]
        [TestCase("/", ExpectedResult = "")]
        public string WhenAsked_ResultIsLastSegment(string path) => FtpPath.GetName(path);
    }
}
