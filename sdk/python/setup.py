from setuptools import setup
from setuptools.dist import Distribution


class BinaryDistribution(Distribution):
    # Marks the wheel as platform-specific so pip selects the correct
    # platform wheel. The CLI binary in _bin/ is not a compiled extension,
    # but it is native — this flag ensures the wheel tag reflects that.
    def has_ext_modules(self):
        return True


setup(distclass=BinaryDistribution)
