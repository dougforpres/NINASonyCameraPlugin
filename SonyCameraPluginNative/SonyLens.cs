using Sony;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sony {
    public class SonyLens {
        private LensInfo _lensInfo;

        public SonyLens(LensInfo info) {
            _lensInfo = info;
        }

        public string Id {
            get {
                return _lensInfo.Id;
            }
        }

        public string Manufacturer {
            get {
                return _lensInfo.Manufacturer;
            }
        }

        public string Model {
            get {
                return _lensInfo.Model;
            }
        }

        // Registry path, not path to open - use "id" for that
        public string Path {
            get {
                return _lensInfo.LensPath;
            }
        }
    }
}
