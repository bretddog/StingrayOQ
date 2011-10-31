/************************************************************************
 * Copyright(c) 2011 finantic All rights reserved.                      *
 *                                                                      *
 * This file is provided as is with no warranty of any kind, including  *
 * the warranty of design, merchantability and fitness for a particular *
 * purpose.                                                             *
 *                                                                      *
 * This software may not be used nor distributed without proper license *
 * agreement.                                                           *
 ************************************************************************/

using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows; // Timer
using System.Windows.Forms;
using Krs.Ats.IBNet96; // Krs.Ats.IBNet96; // IBClient
using OpenQuant.API;

using OQ = OpenQuant.API;
using OpenQuant.API.Plugins;
using IB = Krs.Ats.IBNet96;
using Order = OpenQuant.API.Order;
using OrderStatus = OpenQuant.API.OrderStatus;
using TimeInForce = Krs.Ats.IBNet96.TimeInForce;

namespace finantic.OQPlugins
{
	public class StingrayOQ : UserProvider
    {
        #region Constants
        const string svnRevision = "$Rev: 30 $";
        const string svnDate = "$Date: 2011-10-30 10:46:08 +0100 (Sun, 30 Oct 2011) $";
        const string svnId = "$Id: Stingray.cs 30 2011-10-30 09:46:08Z rene $";
        #endregion // const

        #region private variables
        // Class members

		private bool ordersLoggedIn;

		private Dictionary<string, Instrument> subscribedInstruments;

		private Dictionary<string, SrBrokerOrder> pendingOrders;
		private Dictionary<string, OQ.Order> workingOrders;

        // IBClient
        IBClient ibclient;

        // IBClient Status
        int nextValidId = -1;

        // TWS Status
        DateTime serverTime = DateTime.MinValue;

        // Broker Details, Settings
        Dictionary<string, string> portfolioSettings = new Dictionary<string, string>();
        DateTime lastPfsUpdate = DateTime.MinValue;


        // Orders
        private bool ordersValid = false;
        private DateTime lastOpenOrderUpdate = DateTime.Now;
        private Dictionary<string, SrOrder> openOrders =
            new Dictionary<string, SrOrder>();

        // Positions
        private bool positionsValid = false;
        private DateTime lastPortfolioUpdate = DateTime.MinValue;

        // Executions
        bool executionsComplete = false;
        Dictionary<string, ExecutionInfo> executionList = new Dictionary<string, ExecutionInfo>();
        string latestExecutionID = "";
        DateTime latestExecution = DateTime.MinValue;
        int numNewExecutions = 0;

        // Stingray Threadsave Monitor
        // this data needs a lock(private_lock) -----------------------------------
	    private DataTable accountDetails;
        Dictionary<string, double> activeAccounts = new Dictionary<string, double>();

        Dictionary<string, SrBrokerPosition> openPositions = new Dictionary<string, SrBrokerPosition>();                       
        // end threadsave Monitor -------------------------------------------------

        // OpenQuant State
        BrokerInfo brokerInfo = new BrokerInfo();

        // Watchdog
        System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();

        // Logger
        Logger logger;

        // sync
        object private_lock = new object();
        #endregion

        #region constructor
        public StingrayOQ()
		{
			base.name        = "StingrayOQ";
			base.description = "finantic's Execution Provider for Interactive Brokers";
			base.id          = 129; // Byte
			base.url         = "http://www.finantic.de";


            accountDetails = new DataTable();
            DataColumn[] primaryKey = new DataColumn[3];
            primaryKey[0] = accountDetails.Columns.Add("Account", typeof(string));
            primaryKey[1] = accountDetails.Columns.Add("Key", typeof(string));
            primaryKey[2] = accountDetails.Columns.Add("Currency", typeof(string));
            accountDetails.Columns.Add("Value", typeof(string));

            accountDetails.PrimaryKey = primaryKey;

			ordersLoggedIn = false;

			subscribedInstruments = new Dictionary<string, Instrument>();

			pendingOrders = new Dictionary<string, SrBrokerOrder>();
			workingOrders = new Dictionary<string, OQ.Order>();
		}
        #endregion

        #region Settings
        // Connection -------------------------------------------------------------------------------------------
	    private int clientId = 4262;
        [Category("Connection"),
    Description("A number used to identify the data plugin client. " +
        "Each client MUST connect with a unique clientId"),
   DefaultValue(1234)]
        public int ClientId
        {
            get { return clientId; }
            set { clientId = value; }
        }

	    private string hostname = "127.0.0.1";
	    [Category("Connection"),
	     Description("Name or IP adress of host where TWS is running. Use 127.0.0.1 for local host"),
	     DefaultValue("127.0.0.1")]
	    public string Hostname
	    {
	        get { return hostname; }
	        set { hostname = value; }
	    }

	    private int _port = 7496;
	    [Category("Connection"),
         Description("Port for connection to TWS. Use 7496 as default port."),
         DefaultValue(7496)]
        public int Port
	    {
	        get { return _port; }
	        set { _port = value; }
	    }

	    private string faAccounts = "";
        [Category("TWS"),
Description("Financial Advisor Accounts. Comma separated list of accounts"),
DefaultValue("")]
        public string FAAccounts
        {
            get { return faAccounts; }
            set { faAccounts = value; }
        }

        // Order Defaults
	    private bool _autoTransmit = true;

	    [Category("Order Defaults"),
Description("Orders are automatically transmitted if set to true. If set to false you have to click \"Transmit\" in TWS to transmit the order"),
DefaultValue(true)]
        public bool AutoTransmit
	    {
	        get { return _autoTransmit; }
	        set { _autoTransmit = value; }
	    }

	    private bool _outsideRth = false;
	    [Category("Order Defaults"),
Description("If set to true, allows orders to also trigger or fill outside regular trading hours."),
DefaultValue(false)]
         public bool OutsideRTH
	    {
	        get { return _outsideRth = false; }
	        set { _outsideRth = value; }
	    }


                    // Settings
	    private FinancialAdvisorAllocationMethod _faMethod = FinancialAdvisorAllocationMethod.None;
	    [Category("Order Defaults - FA Allocation"),
Description("Financial advisor allocation method"),
DefaultValue(IB.FinancialAdvisorAllocationMethod.None)]
	    public IB.FinancialAdvisorAllocationMethod FAMethod
	    {
	        get { return _faMethod; }
	        set { _faMethod = value; }
	    }

	    [Category("Order Defaults - FA Allocation"),
Description("Financial advisor allocation group"),
DefaultValue("")]
        public string FAGroup { get; set; } // 

        [Category("Order Defaults - FA Allocation"),
Description("Financial advisor allocation percentage"),
DefaultValue("")]
        public string FAPercentage { get; set; }

        [Category("Order Defaults - FA Allocation"),
Description("Financial advisor allocation profile"),
DefaultValue("")]
        public string FAProfile { get; set; }

	    // Logging ------------------------------------------------------------
	    private LogDestination logDest = LogDestination.File;
        [Category("Misc"),
Description("Specifies where logging information is written to.\r\n"
+ "File: write logging information to StingrayOQ-<day>.log in OQ installation directory.\r\n"
+ "WinEvent: write logging information to Windows Event Logger"),
DefaultValue(LogDestination.File)]
        public LogDestination LogDestination
        {
            get { return logDest; }
            set { logDest = value; }
        }

        private LoggerLevel pluginLogLevel = LoggerLevel.Detail;
	    [Category("Misc"),
	     Description("Specifies how detailed the log entries are\r\n"
	                 + "System: least detailed\r\n"
	                 + "Error: error messages\r\n"
	                 + "Warning: warning messages\r\n"
	                 + "Information: important events\r\n"
	                 + "Detail: additional information, used to resolve problems (involves some performance overhead)\r\n"
	         ),
	     DefaultValue(LoggerLevel.Error)]         
        public LoggerLevel LoggerLevel
        {
            get { return pluginLogLevel; }
            set { pluginLogLevel = value; }
        }
		#endregion //================================== Settings End ===============================================

		#region Connect/Disconnect

	    private bool _isConnected = false;
        protected override bool IsConnected
        {
            get { return _isConnected; }
        }

		protected override void Connect()
		{
			if (IsConnected)
			{
				EmitError("Already connected.");
				return;
			}
            InitLogger();
            logger.LoggerLevel = this.LoggerLevel;
            logger.LogDestination = this.LogDestination;
            tStart("Initialize()");
            string version = Version();
		    string text = "=== StingrayOQ V " + version + " started ===";            
		    info(text);
		    CheckTWS();
            tEnd("Initialize()");
		}

		protected override void Disconnect()
		{
            if (!IsConnected) return; // already dsiconnected
			
            tStart("Disonnect()");
            if (ibclient != null && ibclient.Connected)
            {
                ibclient.Disconnect(); // request disconnect
                info("Disconnected");
            }
            else
            {
                _isConnected = false;
                EmitDisconnected();
            }
            tEnd("Disonnect()");
		}

		protected override void Shutdown()
		{
			Disconnect();
		}


		#endregion

		#region Subscribe/Unsubscribe

        //protected override void Subscribe(Instrument instrument)
        //{
        //    if (!IsConnected)
        //    {
        //        EmitError("Not connected.");

        //        return;
        //    }

        //    if (!subscribedInstruments.ContainsKey(instrument.Symbol))
        //    {
        //        subscribedInstruments.Add(instrument.Symbol, instrument);

        //    }
        //}

        //protected override void Unsubscribe(Instrument instrument)
        //{
        //    if (!IsConnected)
        //    {
        //        EmitError("Not connected.");

        //        return;
        //    }

        //    if (subscribedInstruments.ContainsKey(instrument.Symbol))
        //    {

        //        subscribedInstruments.Remove(instrument.Symbol);
        //    }
        //}


		#endregion

		#region OpenQuant: Orders

        private bool OrderSideToActionSide(OrderSide orderside, out ActionSide action)
        {
            action = ActionSide.Undefined;
            switch(orderside)
            {
                case OrderSide.Buy:
                    action = ActionSide.Buy;
                    break;
                case OrderSide.Sell:
                    action = ActionSide.Sell;
                    break;
                default:
                    return false;
            }
            return true;
        }

        private bool OQOrderTypeToIBOrderType(OQ.OrderType oqtype, out IB.OrderType ibtype)
        {
            ibtype = IB.OrderType.Empty;
            switch(oqtype)
            {
                case OQ.OrderType.Limit:
                    ibtype = IB.OrderType.Limit;
                    break;
                case OQ.OrderType.Market:
                    ibtype = IB.OrderType.Market;
                    break;
                case OQ.OrderType.MarketOnClose:
                    ibtype = IB.OrderType.MarketOnClose;
                    break;
                case OQ.OrderType.Stop:
                    ibtype = IB.OrderType.Stop;
                    break;
                case OQ.OrderType.StopLimit:
                    ibtype = IB.OrderType.StopLimit;
                    break;
                case OQ.OrderType.Trail:
                    ibtype = IB.OrderType.TrailingStop;
                    break;
                case OQ.OrderType.TrailLimit:
                    ibtype = IB.OrderType.TrailingStopLimit;
                    break;               
                default:
                    return false;
            }
            return true;
        }

        private bool OQTimeInForceToIBTimeInForce(OQ.TimeInForce oqtif, out IB.TimeInForce ibtif)
        {
            ibtif = IB.TimeInForce.Undefined;
            switch(oqtif)
            {
                
                case OQ.TimeInForce.Day:
                    ibtif = TimeInForce.Day;
                    break;
                case OQ.TimeInForce.FOK:
                    ibtif = TimeInForce.FillOrKill;
                    break;
                case OQ.TimeInForce.GTC:
                    ibtif = TimeInForce.GoodTillCancel;
                    break;
                case OQ.TimeInForce.GTD:
                    ibtif = TimeInForce.GoodTillDate;
                    break;
                case OQ.TimeInForce.OPG:
                    ibtif = TimeInForce.MarketOnOpen;
                    break;                
            case OQ.TimeInForce.ATC:
            case OQ.TimeInForce.GFS:
            case OQ.TimeInForce.GTX:
            case OQ.TimeInForce.IOC:
            default:
                    return false;
            }
            return true;
        }

        private bool InstrumentTypeToSecurityType(OQ.InstrumentType instrType, out IB.SecurityType secType)
        {
            secType = SecurityType.Undefined;
            switch(instrType)
            {
                case InstrumentType.Bond:
                    secType = SecurityType.Bond;
                    break;
                case InstrumentType.ETF:
                    secType = SecurityType.Stock;
                    break;
                case InstrumentType.FX:
                    secType = SecurityType.Cash;
                    break;
                case InstrumentType.FutOpt:
                    secType = SecurityType.FutureOption;
                    break;
                case InstrumentType.Futures:
                    secType = SecurityType.Future;
                    break;
                case InstrumentType.Index:
                    secType = SecurityType.Index;
                    break;
                case InstrumentType.Option:
                    secType = SecurityType.Option;
                    break;
                case InstrumentType.Stock:
                    secType = SecurityType.Stock;
                    break;
                case InstrumentType.MultiLeg:
                default:
                    return false;
            }
            return true;
        }

        private bool OQFAMethodTOIBFAMethod(OQ.IBFaMethod oqFaMethod,
            out IB.FinancialAdvisorAllocationMethod ibFaMethod)
        {
            ibFaMethod = FinancialAdvisorAllocationMethod.None;
            switch(oqFaMethod)
            {
                case IBFaMethod.AvailableEquity:
                    ibFaMethod = FinancialAdvisorAllocationMethod.AvailableEquity;
                    break;
                case IBFaMethod.EqualQuantity:
                    ibFaMethod = FinancialAdvisorAllocationMethod.EqualQuantity;
                    break;
                case IBFaMethod.NetLiq:
                    ibFaMethod = FinancialAdvisorAllocationMethod.NetLiquidity;
                    break;
                case IBFaMethod.PctChange:
                    ibFaMethod = FinancialAdvisorAllocationMethod.PercentChange;
                    break;
                case IBFaMethod.Undefined:
                default:
                    return false;
            }
            return true;
        }


        private bool FAAllocationMethodTOIBFAMethod(IB.FinancialAdvisorAllocationMethod ibFaMethod,
            out OQ.IBFaMethod oqFaMethod)
        {
            oqFaMethod = IBFaMethod.Undefined;
            switch(ibFaMethod)
            {
                case FinancialAdvisorAllocationMethod.AvailableEquity:
                    oqFaMethod = IBFaMethod.AvailableEquity;
                    break;
                case FinancialAdvisorAllocationMethod.EqualQuantity:
                    oqFaMethod = IBFaMethod.EqualQuantity;
                    break;
                case FinancialAdvisorAllocationMethod.NetLiquidity:
                    oqFaMethod = IBFaMethod.NetLiq;
                    break;
                case FinancialAdvisorAllocationMethod.PercentChange:
                    oqFaMethod = IBFaMethod.PctChange;
                    break;
                case FinancialAdvisorAllocationMethod.None:
                default:
                    return false;
            }
            return true;
        }

	    protected override void Send(OQ.Order order)
	    {
	        if (!CheckTWS()) return;

	        if (!IsConnected)
	        {
	            EmitError("Not connected.");
	            return;
	        }
            // check Account
            if(activeAccounts.Count > 1)
            {
                if(string.IsNullOrWhiteSpace(order.Account))
                {
                    throw new Exception("no account specified");
                }
            }           

	        int orderId;
	        Contract contract = new Contract();
	        IB.Order iborder = new IB.Order();

	        // OrderId
	        int nextId;
	        lock (private_lock)
	        {
	            nextId = nextValidId;
	            nextValidId++;
	        }
	        iborder.OrderId = nextId;

	        // Order Attributes
	        iborder.Account = order.Account;
	        IB.ActionSide ibaction;
	        if (!OrderSideToActionSide(order.Side, out ibaction)) // Buy / Sell
	        {
	            error("unknown order side in Order " + order.Instrument.Symbol);
	            return;
	        }
	        iborder.Action = ibaction;
	        iborder.AuxPrice = (decimal) order.StopPrice; // stop orders only
	        iborder.ClientId = ClientId;
            
	        IB.FinancialAdvisorAllocationMethod faMethod;
	        if (order.IB.FaMethod == IBFaMethod.Undefined) // use Order Defaults form Settings
	        {
	            iborder.FAMethod = FAMethod;
	            iborder.FAGroup = FAGroup;	            
	            iborder.FAPercentage = FAPercentage;
	            iborder.FAProfile = FAProfile;               
	        }
	        else
	        {
                if (!OQFAMethodTOIBFAMethod(order.IB.FaMethod, out faMethod))
                {
                    error("bad FA allocation method in order " + order.Instrument.Symbol);
                }
	            iborder.FAMethod = faMethod;
	            iborder.FAGroup = order.IB.FaGroup;	            
	            iborder.FAPercentage = order.IB.FaPercentage.ToString();
	            iborder.FAProfile = order.IB.FaProfile;
	            OQ.IBFaMethod oqfamethod;
                if (!FAAllocationMethodTOIBFAMethod(iborder.FAMethod, out oqfamethod))
                {
                    error("unknown FA allocation method in settings");
                }
	            order.IB.FaMethod = oqfamethod;
	            order.IB.FaGroup = iborder.FAGroup;
                if (iborder.FAPercentage == "") order.IB.FaPercentage = 0.0;
	            else order.IB.FaPercentage = double.Parse(iborder.FAPercentage, CultureInfo.InvariantCulture);
	            order.IB.FaProfile = order.IB.FaProfile;
	        }
	        // iborder.GoodAfterTime = order.StrategyPrice??
		    // iborder.GoodTillDate = order.ExpireTime;
            iborder.Hidden = order.IB.Hidden;
		    iborder.LimitPrice = (decimal)order.Price;
            // iborder.MinQty
            // iborder.NbboPriceCap
            // = order.IB.DisplaySize;
		    iborder.OcaGroup = order.OCAGroup;
 		    IB.OrderType ibtype;
            if(!OQOrderTypeToIBOrderType(order.Type, out ibtype))
            {
                error("bad order type in order " + order.Instrument.Symbol);
                return;
            }
		    iborder.OrderType = ibtype;
            // iborder.Origin
            // iborder.OutsideRth = order.???
            // iborder.OverridePercentageConstraints = ???
            // iborder.ParentId
            // iborder.PercentOffset
            // iborder.PermId
            // iborder.ReferencePriceType
            // iborder.Rule80A
            // iborder.StartingPrice
            // iborder.StockRangeLower
            // iborder.StockRangeUpper
            // iborder.StockRefPrice
            // iborder.SweepToFill
	        IB.TimeInForce ibtif;
            if (!OQTimeInForceToIBTimeInForce(order.TimeInForce, out ibtif))
            {
                error("unknown time in force in order " + order.Instrument.Symbol);
                return;
            }
	        iborder.Tif = ibtif;
		    iborder.TotalQuantity = (int)Math.Round(order.Qty);
            // iborder.TrailStopPrice = order.???
		    iborder.Transmit = AutoTransmit;
            // iborder.TriggerMethod
            // iborder.Volatility
            // iborder.VolatilityType
            // iborder.WhatIf
            
            // Contract
            // contract.ComboLegs
            // contract.ComboLegsDescription
            // contract.ContractId
		    contract.Currency = order.Instrument.Currency;
		    contract.Exchange = order.Instrument.Exchange;
            contract.Expiry = order.Instrument.Maturity.ToString("yyyyMM"); // YYYYMM
            // contract.IncludeExpired
            // contract.LocalSymbol
		    contract.Multiplier = order.Instrument.Factor.ToString();
            // contract.PrimaryExchange
            // contract.Right
            // contract.SecId
		    IB.SecurityType secType;
            if (!InstrumentTypeToSecurityType(order.Instrument.Type, out secType))
            {
                error("bad instrument type in order " + order.Instrument.Symbol);
            }
		    contract.SecurityType = secType;
	        contract.Strike = order.Instrument.Strike;
	        contract.Symbol = order.Instrument.Symbol;
            // contract.UnderlyingComponent

            // parse advanced order attributes
            if (!string.IsNullOrWhiteSpace(order.Text))
            {
                string[] fields = order.Text.Split(';');
                foreach(string field in fields)
                {
                    if (!field.Contains("=") && !field.Contains(":")) continue;
                    string[] nameval = field.Split(new char[] {'=', ':'});
                    string name = nameval[0].Trim().ToLower();
                    string value = nameval[1].Trim();
                    switch(name)
                    {
                        case "orderref":
                            iborder.OrderRef = value;
                            break;
                        default:
                            error("unknown name \"" + name + "\" in text of order " + order.Instrument.Symbol);
                            break;
                    }
                }
            }
	        // send to TWS
            info("PlaceOrder("
                + "ID=" + iborder.OrderId.ToString()
                + ", Orderref=" + (iborder.OrderRef  ?? "")
                + ", Symbol=" + contract.Symbol
                + ", Action=" + iborder.Action.ToString()
                + ", Size=" + iborder.TotalQuantity.ToString()
                + ", OrderType=" + iborder.OrderType.ToString()
                + ", limit price=" + iborder.LimitPrice.ToString()
                + ", Aux price=" + iborder.AuxPrice.ToString()
                + ")");

            ibclient.PlaceOrder(iborder.OrderId, contract, iborder);
            // Set Infos back to OQ.Order
            order.OrderID = iborder.OrderId.ToString();
            order.ClientID = iborder.ClientId.ToString();
            // Remember active orders
	        workingOrders[iborder.OrderId.ToString()] = order;
		}

		protected override void Cancel(OQ.Order order)
		{
            if (!CheckTWS()) return;
			if (!IsConnected)
			{
				EmitError("Not connected.");
				return;
			}
            // TODO: TryParse
		    int orderId = int.Parse(order.OrderID);
            ibclient.CancelOrder(orderId);
		}

        protected override void Replace(OQ.Order order, double newQty, double newPrice, double newStopPrice)
        {
            if (!CheckTWS()) return;
            if (!IsConnected)
            {
                EmitError("Not connected.");
                return;
            }
            Cancel(order);
            order.Qty = newQty;
            order.Price = newPrice;
            order.StopPrice = newStopPrice;
            Send(order);
        }
		#endregion

		#region OpenQuant: GetBrokerInfo

		protected override BrokerInfo GetBrokerInfo()
		{
            // info("GetBrokerInfo called");
		    int numAccounts = 0;
		    int numOpenPositions = 0;
		    int numPendingOrders = 0;
            BrokerInfo brokerInfo = new BrokerInfo();
            if (!IsConnected) return brokerInfo;
            
            lock (private_lock)
            {
                foreach (string acctName in activeAccounts.Keys)
                {
                    brokerInfo.AddAccount(acctName);
                    numAccounts++;

                    BrokerAccount oqAccount = brokerInfo.Accounts[acctName];
                    // Account Details
                    foreach (DataRow row in accountDetails.Rows)
                    {
                        if ((string)row["Account"] != acctName) continue;
                        string key = (string) row["Key"];
                        string value = (string) row["Value"];
                        oqAccount.AddField(key, (string) row["Currency"], value);
                        if (key == "BuyingPower")
                        {
                            double buyingPower;
                            if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture,
                                                 out buyingPower))
                            {
                                error("can't parse buying Power: " + value);
                            }
                            else
                            {
                                //logger.AddLog(LoggerLevel.Detail, "*** received Buying Power: " + buyingPower.ToString());
                                // activeAccounts[e.AccountName] = buyingPower;
                                oqAccount.BuyingPower = buyingPower;
                            }
                        }
                    }
                    // Positions
                    foreach(SrBrokerPosition brpos in openPositions.Values)
                    {
                        if (brpos.Fields["Account"] != acctName) continue;
                        BrokerPosition pos = oqAccount.AddPosition();
                        numOpenPositions++;
                        pos.Currency = brpos.Currency;
                        pos.Exchange = brpos.Exchange;
                        pos.InstrumentType = brpos.InstrumentType;
                        pos.LongQty = brpos.LongQty;
                        pos.Maturity = brpos.Maturity;
                        pos.PutCall = brpos.PutCall;
                        pos.Qty = brpos.Qty;
                        pos.ShortQty = brpos.ShortQty;
                        pos.Strike = brpos.Strike;
                        pos.Symbol = brpos.Symbol;
                        // Fields
                        foreach(KeyValuePair<string,string> kvp in brpos.Fields)
                        {
                            pos.AddCustomField(kvp.Key, kvp.Value);
                        }
                    }  
                    // Orders
                    foreach (SrBrokerOrder srorder in pendingOrders.Values)
                    {
                        if (srorder.Fields["Account"] != acctName) continue;
                        BrokerOrder ord = oqAccount.AddOrder();
                        numPendingOrders++;
                        ord.Currency = srorder.Currency;
                        ord.Exchange = srorder.Exchange;
                        ord.InstrumentType = srorder.InstrumentType;
                        ord.OrderID = srorder.OrderId;
                        ord.Price = srorder.Price;
                        ord.Qty = srorder.Qty;
                        ord.Side = srorder.Side;
                        ord.Status = srorder.Status;
                        ord.StopPrice = srorder.StopPrice;
                        ord.Symbol = srorder.Symbol;
                        ord.Type = srorder.Type;
                        // Fields
                        foreach (KeyValuePair<string, string> kvp in srorder.Fields)
                        {
                            ord.AddCustomField(kvp.Key, kvp.Value);
                        }
                    }                    
                }
            }
            info("BrokerInfo: " 
                + numAccounts + " Accounts, "
                + numOpenPositions + " Positions, "
                + numPendingOrders + " Orders");
		    return brokerInfo;
		}

      

	    #endregion

        #region CheckTWS, openConnection

        private bool CheckTWS()
        {
            if (ibclient != null && ibclient.Connected)
            {
                return true;
            }
            tStart("CheckTWS()");
            string lastError = "";
            try
            {
                if (openConnection())
                {
                    tEnd("CheckTWS()");
                    return true;
                }
            }
            catch (Exception ex)
            {
               lastError = "openConnection: " + ex.Message;               
            }
            string text = "StingrayOQ: could not connect to server "
                + Hostname + ", port " + Port.ToString()
                + ", with ClientId " + clientId.ToString();

            if (lastError != "") text += "\r\nError: " + lastError;
            error(text);            
            tEnd("CheckTWS()");
            return false;
        }

        #region open connection
        private bool openConnection()
        {
            string text; 
            ordersValid = false;
            positionsValid = false;

            tStart("openConnection()");
            // check TWS

            // open socket connection
            if (ibclient == null)
            {
                logger.AddLog(LoggerLevel.Detail, "new IBClient");

                ibclient = new IBClient();
                // DEBUG
                ibclient.ThrowExceptions = true;
                ibclient.Error += ibclient_Error;
                ibclient.ConnectionClosed += ibclient_ConnectionClosed;
                ibclient.CurrentTime += new EventHandler<CurrentTimeEventArgs>(ibclient_CurrentTime);
                ibclient.UpdateAccountValue += new EventHandler<UpdateAccountValueEventArgs>(ibclient_UpdateAccountValue);
                ibclient.NextValidId += new EventHandler<NextValidIdEventArgs>(ibclient_NextValidId);
                ibclient.OpenOrder += new EventHandler<OpenOrderEventArgs>(ibclient_OpenOrder);
                ibclient.OrderStatus += new EventHandler<OrderStatusEventArgs>(ibclient_OrderStatus);
                ibclient.UpdatePortfolio += new EventHandler<UpdatePortfolioEventArgs>(ibclient_UpdatePortfolio);
                ibclient.ExecDetails += new EventHandler<ExecDetailsEventArgs>(ibclient_ExecDetails);
                ibclient.AccountDownloadEnd += new EventHandler<AccountDownloadEndEventArgs>(ibclient_AccountDownloadEnd);
                ibclient.ExecutionDataEnd += new EventHandler<ExecutionDataEndEventArgs>(ibclient_ExecutionDataEnd);
                ibclient.OpenOrderEnd += new EventHandler<EventArgs>(ibclient_OpenOrderEnd);
                ibclient.ManagedAccounts += new EventHandler<ManagedAccountsEventArgs>(ibclient_ManagedAccounts);                
            }

            if (!ibclient.Connected)
            {
                logger.AddLog(LoggerLevel.Detail, "Connect");
                ibclient.Connect(Hostname, Port, clientId);

                if (!ibclient.Connected) return false;
                ibclient.ThrowExceptions = false;
                logger.AddLog(LoggerLevel.Detail, "Connected");

                //// sync PC clock to IB Server time
                //if (settings.SynchronizeMachineTimeToIBServerTime
                //    == MachineTime.SynchToIBServerTime)
                //{
                //    logger.AddLog(LoggerLevel.Detail, "Sync PC clock");
                //    string dtStr = ibclient.TwsConnectionTime;
                //    DateTime dt = DateTime.ParseExact(dtStr.Substring(0, 17),
                //        "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture);
                //    SetMachineTimeToServerTime(dt);
                //}

                //// nicht f�r Gateway FIX
                //if (settings.DataHost == "") ibclient.SetServerLogLevel(settings.LogLevel);

                // cancel old market subscriptions
                //logger.AddLog(LoggerLevel.Detail, "Cancel old market data subscriptions");
                //for (int i = 10000; i < 10200; i++)
                //{
                    // DEBUG
                    // ibclient.CancelMktData(i);
                //}
                // successful started, reset reconnection counter
                watchdog.Start();
            }

            ibclient.RequestManagedAccts();

            //// wait for account list or error
            int numAccounts = 0;
            for (int timeout = 20; timeout>0 ; timeout-- )
            {
                lock (private_lock)
                {
                    numAccounts = activeAccounts.Count;
                }
                if (numAccounts > 0) break;
                Thread.Sleep(500);                
            }
            info(numAccounts + " Accounts found");    
            RequestAccountUpdates();
            lastPortfolioUpdate = DateTime.Now;
            ibclient.RequestOpenOrders();
            lastOpenOrderUpdate = DateTime.Now;

            ibclient.RequestIds(1);
            // serverTime = ibclient.TwsConnectionTime;
            ibclient.RequestCurrentTime();
          
            text = Version();
            text += ": connected. Server Version: "
                + ibclient.ServerVersion.ToString()
                + ", Connection Time from TWS: "
                + ibclient.TwsConnectionTime;
            logger.AddLog(LoggerLevel.Information, text);
            info(text);

            logger.AddLog(LoggerLevel.Detail, "*** Connected ***");
            _isConnected = true;
            EmitConnected();
            tEnd("openConnection()");
            return true;
        }

        void ibclient_ManagedAccounts(object sender, ManagedAccountsEventArgs e)
        {
            string[] acctList = e.AccountsList.Split(',');
            lock(private_lock)
            {
                foreach(string acct in acctList)
                {
                    if(string.IsNullOrWhiteSpace(acct)) continue;
                    string acct2 = acct.Trim();
                    if(!activeAccounts.ContainsKey(acct2)) activeAccounts.Add(acct2, 0.0);
                }
            }
        }
        #endregion


        #endregion

        #region IBClient Events
        void ibclient_ExecDetails(object sender, ExecDetailsEventArgs e)
        {
            lock (executionList)
            {
                DateTime exTime = DateTime.ParseExact(e.Execution.Time, "yyyyMMdd  HH:mm:ss",
                    CultureInfo.InvariantCulture);

                if (exTime > latestExecution)
                {
                    latestExecution = exTime;
                    latestExecutionID = e.Execution.ExecutionId;
                }
                if (!executionList.ContainsKey(e.Execution.ExecutionId))
                {
                    ExecutionInfo ei = new ExecutionInfo();

                    ei.contract = e.Contract;
                    ei.execution = e.Execution;
                    ei.OrderId = e.OrderId;
                    ei.symbol = e.Contract.Symbol;
                    executionList.Add(
                        e.Execution.ExecutionId,
                        ei);
                    numNewExecutions++;
                }
            }
        }

        void ibclient_TickGeneric(object sender, TickGenericEventArgs e)
        {
            // MessageBox.Show("ibclient_TickGeneric: TickType=" + e.TickType.ToString());
            //throw new Exception("The method or operation is not implemented.");
        }

        void ibclient_ExecutionDataEnd(object sender, ExecutionDataEndEventArgs e)
        {
            // we got all executions
            executionsComplete = true;
        }

        void ibclient_UpdateAccountValue(object sender, UpdateAccountValueEventArgs e)
        {
            try
            {
                ibclient_UpdateAccountValue2(sender, e);
            }
            catch (Exception ex)
            {
                logger.AddLog(LoggerLevel.Error, "ibclient_UpdateAccountValue2: " + ex.Message);
            }
        }

	    void ibclient_UpdateAccountValue2(object sender, UpdateAccountValueEventArgs e)
        {

            //tStart("ibclient_UpdateAccountValue(): "
            //       + "Acct: " + e.AccountName
            //       + ", Curr: " + e.Currency
            //       + ", " + e.Key + ": " + e.Value);
	        string currency = "";
            if (e.Currency != null) currency = e.Currency;
            lock(private_lock)
            {
                if (!activeAccounts.ContainsKey(e.AccountName))
                {
                    activeAccounts.Add(e.AccountName, 0.0);               
                }
                DataRow row = accountDetails.Rows.Find(new string[] { e.AccountName, e.Key, currency });
                if (row == null)
                {
                    row = accountDetails.NewRow();
                    row["Account"] = e.AccountName;
                    row["Key"] = e.Key;
                    row["Currency"] = currency;
                    accountDetails.Rows.Add(row);
                }
                row["Value"] = e.Value;
            }
            return;

            string pfsKey = e.Key + ";" + e.AccountName + ";" + e.Currency;
            string pfsValue = e.Value;

            if (!activeAccounts.ContainsKey(e.AccountName))
            {
                activeAccounts.Add(e.AccountName, 0.0);
               
            }
            BrokerAccount oqAccount = brokerInfo.Accounts[e.AccountName];

            if(!oqAccount.Fields.Contains(e.Key, e.Currency))
            {
                oqAccount.AddField(e.Key, e.Currency, e.Value);
            }
            else
            {
                // oqAccount.Fields.   [e.Key, e.Currency] = e.Value;
                // OQBUG
                // can't set new value ???
            }            
            lock (portfolioSettings)
            {
                if (portfolioSettings.ContainsKey(pfsKey))
                {
                    // throw new Exception("duplicate Key: " + pfsKey);
                }
                portfolioSettings[pfsKey] = pfsValue;
            }
            lastPfsUpdate = DateTime.Now;

            // shortcut
            if (e.Key == "BuyingPower")
            {
                tStart("ibclient_UpdateAccountValue(): "
                + "Acct: " + e.AccountName
                + ", Curr: " + e.Currency
                + ", " + e.Key + ": " + e.Value);
                lock (private_lock)
                {
                    double buyingPower;
                    if (!double.TryParse(e.Value, NumberStyles.Any, CultureInfo.InvariantCulture,
                        out buyingPower))
                    {
                        error("can't parse buying Power: " + e.Value);
                    }
                    else
                    {
                        logger.AddLog(LoggerLevel.Detail, "*** received Buying Power: " + buyingPower.ToString());
                        activeAccounts[e.AccountName] = buyingPower;
                        oqAccount.BuyingPower = buyingPower;
                    }
                }
                tEnd("ibclient_UpdateAccountValue()");
            }
            return;
#if FALSE
            switch (e.Key)
            {
                case "BuyingPower":
                    lock (private_lock)
                    {
                        if (!double.TryParse(e.Value, out buyingPower))
                        {
                            error("can't parse buying Power: " + e.Value);
                        }
                        else
                        {
                            logger.AddLog(LoggerLevel.Detail, "*** received Buying Power: " + buyingPower.ToString());
                        }
                    }
                    break;
                case "TotalCashBalance":
                    if (e.Currency == "BASE")
                    {
                        //info("TotalCashBalance: " + e.Value);
                    }
                    break;
                case "NetLiquidationByCurrency":
                    if (e.Currency == "BASE")
                    {
                        //info("NetLiquidation: " + e.Value);
                    }
                    break;
                case "AccountCode":
                    info("Account: " + e.Value);
                    break;
                default:
                    break;
            }
            tEnd("ibclient_UpdateAccountValue()");
#endif
        }

        void ibclient_AccountDownloadEnd(object sender, AccountDownloadEndEventArgs e)
        {
            tStart("ibclient_AccountDownloadEnd(" + e.AccountName + ")");
            lock (private_lock)
            {
                positionsValid = true;
            }

            // activeAccounts

            // stop account subscription
            // ibclient.RequestAccountUpdates(false, e.AccountName);
            tEnd("ibclient_AccountDownloadEnd");
        }

        void ibclient_OpenOrderEnd(object sender, EventArgs e)
        {
            tStart("ibclient_OpenOrderEnd()");
            lock (private_lock)
            {
                ordersValid = true;
            }
            tEnd("ibclient_OpenOrderEnd");
        }

        void ibclient_OrderStatus(object sender, OrderStatusEventArgs e)
        {
            tStart("ibclient_OrderStatus()");
            if (true /*pluginIsBroker*/)
            {
                // DEBUG
                LoggerLevel llevel = LoggerLevel.Information;
                if (true /*e.Status == OrderStatus.Submitted*/) llevel = LoggerLevel.Error;
                logger.AddLog(llevel, "ibclient_OrderStatus: received from TWS "
                    + " ID: " + e.OrderId.ToString()
                    + ", ClientID: " + e.ClientId.ToString()
                    + ", Filled: " + e.Filled.ToString()
                    + ", Remaining: " + e.Remaining
                    + ", Status: " + e.Status);
               
                bool request = false;
                lock (private_lock)
                {
                    if (DateTime.Now > lastPortfolioUpdate + TimeSpan.FromMinutes(3.0))
                    {
                        request = true;
                    }
                }
                // DEBUG: switch off
                //if (request && ibclient != null && ibclient.Connected)
                //{
                //    logger.AddLog(LoggerLevel.Information,
                //        "** PortfolioUpdate older than 3 Mins ("
                //        + lastPortfolioUpdate.ToString("T")
                //        + "), request new ***");
                //    RequestAccountUpdates();

                //    //lastPortfolioUpdate = DateTime.Now;
                //}
            }
            string orderId = e.OrderId.ToString();

            OQ.Order oqorder = null;
            lock (private_lock)
            {
                // find matching order
                if (!workingOrders.ContainsKey(orderId))
                {
                    info("ibclient_OrderStatus: unknown order: " + e.OrderId.ToString());
                    
                    tEnd("ibclient_OrderStatus()  unknown order");
                    return;
                }
                // srorder = openOrders[e.OrderId.ToString()];
                oqorder = workingOrders[orderId];
            }
 
            lock (private_lock)
            {
                if (e.ClientId != clientId)
                {
                    error("OrderStatus: Client Id mismatch: " + e.ClientId.ToString());
                    tEnd("ibclient_OrderStatus()");
                    return;
                }
            }            

            switch(e.Status)
            {
                case IB.OrderStatus.ApiCancelled:
                case IB.OrderStatus.Canceled:
                    EmitCancelled(oqorder);
                    break;
                case IB.OrderStatus.ApiPending:
                case IB.OrderStatus.PendingSubmit:
                case IB.OrderStatus.Submitted:
                    if (e.Filled == 0)
                    {
                        EmitAccepted(oqorder);
                    }
                    else if(e.Filled > 0)
                    {
                        ReportFill(oqorder, e.Filled, e.Remaining, (double)e.LastFillPrice);
                    }
                    break;
                case IB.OrderStatus.Filled:
                case IB.OrderStatus.PartiallyFilled:
                    ReportFill(oqorder, e.Filled, e.Remaining, (double) e.LastFillPrice);                   
                    break;
                case IB.OrderStatus.PendingCancel:
                    EmitPendingCancel(oqorder);
                    break;
                default:
                case IB.OrderStatus.Unknown:
                case IB.OrderStatus.None:
                case IB.OrderStatus.Inactive:
                    // ignore
                    return;
            }
            //try
            //{
           //    string information;

            //    information = "ID: " + orderId;
            //    if (reorder != null)
            //    {
            //        if (reorder.OrderSymbol != null)
            //        {
            //            information += ", " + reorder.OrderSymbol.Name;
            //        }
            //        else
            //        {
            //            information += ", Symbol==<null>";
            //        }
            //    }
            //    else
            //    {
            //        information += ", <null>";
            //    }
            //    information += ", Status: " + e.Status.ToString();
            //    switch (e.Status)
            //    {
            //        case OrderStatus.Inactive:
            //        // order is rejected by TWS
            //        // example: Margin requirements
            //        // RE does not understand "BrokerOrderState.Invalid"
            //        // we have to cancel this order in RE
            //        // fall through to Cancel
            //        case OrderStatus.Canceled:
            //        case OrderStatus.ApiCancelled:
            //            reorder.OrderState = BrokerOrderState.Cancelled;
            //            lock (private_lock)
            //            {
            //                if (openOrders.ContainsKey(orderId))
            //                {
            //                    openOrders[orderId].reorder.OrderState = BrokerOrderState.Cancelled;
            //                    openOrders.Remove(orderId);
            //                }
            //            }
            //            break;
            //        case OrderStatus.Filled:
            //            reorder.OrderState = BrokerOrderState.Filled;
            //            srorder.acknowledged = true;
            //            break;
            //        case OrderStatus.PendingCancel:
            //            reorder.OrderState = BrokerOrderState.PendingCancel;
            //            // probably already filled
            //            break;
            //        case OrderStatus.PendingSubmit:
            //        case OrderStatus.ApiPending:
            //            // probably already filled
            //            break;
            //        case OrderStatus.PreSubmitted:
            //            // no matching RE status
            //            return;
            //        case OrderStatus.Submitted:
            //            // update acknowledge
            //            srorder.SubmittedDate = DateTime.Now;
            //            srorder.acknowledged = true;
            //            reorder.OrderState = BrokerOrderState.Submitted;
            //            reorder.SubmittedDate = DateTime.Now
            //                + TimeSpan.FromHours(settings.BarTimeStampCorrection); // Timezone
            //            logger.AddLog(LoggerLevel.Error, "ibclient_OrderStatus: Acknowledged! "
            //                + " ID: " + e.OrderId.ToString());
            //            break;
            //        case OrderStatus.None:
            //        case OrderStatus.Unknown:
            //        default:
            //            error("Unknown OrderStatus " + e.Status.ToString());
            //            return;
            //    }

            //    if (reorder.OrderSymbol == null) return;

            //    // check available fills
            //    long alreadyFilled = CheckFill(reorder);

            //    // Is this a new fill?
            //    if (e.Status == OrderStatus.Filled
            //        || e.Status == OrderStatus.Submitted
            //        || e.Status == OrderStatus.PendingSubmit
            //        || e.Status == OrderStatus.ApiPending
            //        || e.Status == OrderStatus.PendingCancel)
            //    {
            //        if (alreadyFilled == e.Filled)
            //        {
            //            // Console.WriteLine("Order " + e.OrderId + " known Fill -> IGNORED");
            //            tEnd("ibclient_OrderStatus()  known fill");
            //            return;
            //        }
            //    }
            //    // new fill
            //    Fill fill = new Fill();
            //    fill.FillDateTime = DateTime.Now
            //        + TimeSpan.FromHours(settings.BarTimeStampCorrection); // Timezone
            //    if (e.Filled > alreadyFilled)
            //    {
            //        Price p = new Price((double)e.LastFillPrice, (double)e.LastFillPrice);
            //        fill.Price = p;
            //        fill.Commission = 0.0;
            //        fill.Quantity = e.Filled - alreadyFilled;
            //        reorder.Fills.Add(fill);
            //    }

            //    if (true /*e.Status == OrderStatus.Filled*/)
            //    {
            //        // The price of the trade converted to the account currency, using the
            //        // current exchange rate at the time the trade took place. 
            //        // fill.Price.AccountPrice = ??
            //        // The price of the trade in the symbol's native currency. 
            //        information += ", FillPrice: " + e.AverageFillPrice.ToString();
            //        information += ", Filled: " + e.Filled.ToString();

            //        // fill.Commission // ???
            //        if (e.LastFillPrice != e.AverageFillPrice)
            //        {
            //            information += ", lastFillPrice: " + e.LastFillPrice.ToString();
            //        }
            //        if (e.Remaining != 0)
            //        {
            //            information += ", remaining: " + e.Remaining.ToString();
            //        }
            //        alreadyFilled = CheckFill(reorder);
            //        if (alreadyFilled != e.Filled)
            //        {
            //            Console.Error.WriteLine("Fill Error: alreadyFilled = "
            //            + alreadyFilled.ToString()
            //            + " != e.Filled = " + e.Filled.ToString());
            //        }
            //    }
            //    if (e.WhyHeld != null && e.WhyHeld.Trim() != "")
            //    {
            //        information += ", WhyHeld: '" + e.WhyHeld + "'";
            //    }
            //    logger.AddLog(LoggerLevel.Information, "OrderUpdate: " + information);
            //    if (reorder.OrderSymbol != null)
            //    {
            //        SendOrderUpdate(reorder, alreadyFilled, fill, information);
            //    }
            //}
            //catch (Exception ex)
            //{
            //    MessageBox.Show("ibclient_OrderStatus: Exception: "
            //    + ex.Message + "\r\n" + ex.StackTrace);
            //    tEnd("ibclient_OrderStatus() Exception");
            //    return;
            //}
            tEnd("ibclient_OrderStatus()");
        }

	    private void ReportFill(Order oqorder, int filled, int remaining, double lastFillPrice)
	    {
	        OrderStatusEventArgs e;
	        string orderId;

            info("ReportFill: Order vor Fill " + oqorder.Instrument.Symbol
                 + " Id=" + oqorder.OrderID
	             + ", CumQty=" + oqorder.CumQty
	             + ", Qty=" + oqorder.Qty
	             + ", LastQty=" + oqorder.LastQty
	             + ", LeavesQty=" + oqorder.LeavesQty);
	        // in order the current fill state is stored:
	        //  oqorder.Qty: overall/planne order size. does not change with fills
	        //  oqorder.CumQty: sum of all fills, increases with every fill after EmitFill: oqorder.CumQty == e.Filled
	        //  oqorder.LeavesQty: remaining shares after fill. oqorder.LeavesQty := Qty - CumQty
	        int fillqty = filled - (int) Math.Round(oqorder.CumQty);
	        if (fillqty > 0)
	        {
                EmitFilled(oqorder, lastFillPrice, fillqty);
	            if (fillqty != (int) Math.Round(oqorder.LastQty))
	            {
	                error("Bad Fill State in order " + oqorder.Instrument.Symbol
                          + ", OrderId=" + oqorder.OrderID
	                      + ": after fill: FillQty(" + fillqty + ") != OQ.LastQty(" + oqorder.LastQty + ")");
	            }
	        }
	        // check fill state
	        if (filled != (int) Math.Round(oqorder.CumQty))
	        {
	            error("Bad Fill State in order " + oqorder.Instrument.Symbol
                      + ", OrderId=" + oqorder.OrderID
	                  + ": after fill: IB.Filled(" + filled + ") != OQ.CumQty(" + oqorder.CumQty + ")");
	        }
	        if (remaining != (int) Math.Round(oqorder.LeavesQty))
	        {
	            error("Bad Fill State in order " + oqorder.Instrument.Symbol
                      + ", OrderId=" + oqorder.OrderID
	                  + ": after fill: IB.Remaining(" + remaining + ") != OQ.LeavesQty(" + oqorder.LeavesQty + ")");
	        }

            info("ReportFill: Order nach Fill fillqty=" + fillqty + "  " + oqorder.Instrument.Symbol
                 + " Id=" + oqorder.OrderID
	             + ", CumQty=" + oqorder.CumQty
	             + ", Qty=" + oqorder.Qty
	             + ", LastQty=" + oqorder.LastQty
	             + ", LeavesQty=" + oqorder.LeavesQty);
	    }

        bool SecurityTypeToInstrumentType(SecurityType secType, out InstrumentType result)
        {
            result = InstrumentType.Stock; // default
            switch(secType)
            {               
                case SecurityType.Bond:
                    result = InstrumentType.Bond;
                    break;
                case SecurityType.Cash:
                    result = InstrumentType.FX;
                    break;
                case SecurityType.Future:
                    result = InstrumentType.Futures;
                    break;    
                case SecurityType.FutureOption:
                case SecurityType.Index:
                    result = InstrumentType.Index;
                    break;
                case SecurityType.Option:
                    result = InstrumentType.FutOpt;
                    break;
                case SecurityType.Stock:
                    result = InstrumentType.Stock;
                    break;
                case SecurityType.Bag:
                case SecurityType.Undefined:                    
                    return false;
            }
            return true;
        }

        void ibclient_UpdatePortfolio(object sender, UpdatePortfolioEventArgs e)
        {
            try
            {
                ibclient_UpdatePortfolio2(sender, e);
            }
            catch (Exception ex)
            {
                error("ibclient_UpdatePortfolio2: " +  ex.Message);
            }
        }

	    void ibclient_UpdatePortfolio2(object sender, UpdatePortfolioEventArgs e)
        {
            tStart("ibclient_UpdatePortfolio()");
            string ts;
            lock (private_lock)
            {
                lastPortfolioUpdate = DateTime.Now;
                ts = lastPortfolioUpdate.ToString("T");
            }
            // positions: number shares, prices, PnL
            // DEBUG was .Information
            logger.AddLog(LoggerLevel.Error, "UpdatePortfolio (timestamp="
                + ts
                + "): "
                + e.Contract.Symbol + "/" + e.Contract.Currency + ": "
                + " Pos: " + e.Position.ToString()
                + "@" + e.MarketPrice.ToString("N4")
                + ", PnL: " + e.UnrealizedPnl.ToString());

            //return;

            string symbol = e.Contract.Symbol;
            if (symbol == null) return;

            SrBrokerPosition brokerPos = new SrBrokerPosition();
            brokerPos.Symbol = symbol;
            brokerPos.Currency = e.Contract.Currency;
            brokerPos.Exchange = e.Contract.PrimaryExchange;
            if(!SecurityTypeToInstrumentType(e.Contract.SecurityType, out brokerPos.InstrumentType))
            {
                error("bad security type in position " + symbol);
                return;
            }
            if (e.Position >= 0)
            {
                brokerPos.LongQty = e.Position;
            }
            else
            {
                brokerPos.ShortQty = -e.Position;
            }
	        brokerPos.Qty = e.Position;
	        // brokerPos.Maturity;
	        // brokerPos.PutCall;
	        brokerPos.Strike = e.Contract.Strike;
            // alles andere in Feldern
            brokerPos.AddCustomField("Account", e.AccountName);
            brokerPos.AddCustomField("AverageCost", e.AverageCost.ToString("N4"));
            brokerPos.AddCustomField("MarketPrice", e.MarketPrice.ToString("N4"));
            brokerPos.AddCustomField("MarketValue", e.MarketValue.ToString());
            brokerPos.AddCustomField("RealizedPnl", e.RealizedPnl.ToString("N2"));
            brokerPos.AddCustomField("UnrealizedPnl", e.UnrealizedPnl.ToString("N2"));
            brokerPos.AddCustomField("ContractId", e.Contract.ContractId.ToString());
            brokerPos.AddCustomField("LocalSymbol", e.Contract.LocalSymbol);
            brokerPos.AddCustomField("Expiry", e.Contract.Expiry);
            brokerPos.AddCustomField("Multiplier", e.Contract.Multiplier);
            // brokerPos.AddCustomField("Exchange", e.Contract.Exchange);
            // brokerPos.AddCustomField("Right", e.Contract.Right.ToString());
            // brokerPos.AddCustomField("SecId", e.Contract.SecId);          
        
            lock (openPositions)
            {
                string key = e.AccountName + "|" + symbol;
                if (openPositions.ContainsKey(key))
                {
                    logger.AddLog(LoggerLevel.Detail, "known position");
                    openPositions[key] = brokerPos;
                }
                else
                {
                    logger.AddLog(LoggerLevel.Detail, "unknown position (added)");
                    openPositions.Add(key, brokerPos);
                }
            }
            tEnd("ibclient_UpdatePortfolio()");
        }

        public bool ActionSideToOrderSide(ActionSide action, out OrderSide side)
        {
            side = OrderSide.Buy;
            switch(action)
            {
                case ActionSide.Buy:
                    side = OrderSide.Buy;
                    break;
                case ActionSide.Sell:
                    side = OrderSide.Sell;
                    break;
                case ActionSide.SShort:
                    side = OrderSide.Sell;
                    break;
                case ActionSide.Undefined:
                default:
                    return false;
            }
            return true;
        }

        private bool OrderStateToOrderStatus(IB.OrderStatus ibstatus, out OQ.OrderStatus status)
        {
            status = OQ.OrderStatus.New;
            switch(ibstatus)
            {
                case IB.OrderStatus.ApiCancelled:
                case IB.OrderStatus.Canceled:
                    status = OQ.OrderStatus.Cancelled;
                    break;
                case IB.OrderStatus.Inactive:
                case IB.OrderStatus.ApiPending:
                case IB.OrderStatus.PendingSubmit:
                case IB.OrderStatus.PreSubmitted:
                case IB.OrderStatus.Submitted:
                    status = OQ.OrderStatus.PendingNew;
                    break;
                case IB.OrderStatus.Filled:
                    status = OQ.OrderStatus.Filled;
                    break;
                case IB.OrderStatus.PartiallyFilled:                
                    status = OQ.OrderStatus.PartiallyFilled;
                    break;
                case IB.OrderStatus.PendingCancel:
                    status = OQ.OrderStatus.PendingCancel;
                    break;
                default:
                case IB.OrderStatus.Unknown:
                    return false;
            }
            return true;
        }

        private bool OrderTypeToOrderType(IB.OrderType ibordertype, out OQ.OrderType oqordertype)
        {
            oqordertype = OQ.OrderType.Market;
            switch(ibordertype)
            {
                case IB.OrderType.Limit:
                case IB.OrderType.LimitOnClose:
                    oqordertype = OQ.OrderType.Limit;
                    break;
                case IB.OrderType.Market:               
                    oqordertype = OQ.OrderType.Market;
                    break;
                case IB.OrderType.MarketOnClose:
                    oqordertype = OQ.OrderType.MarketOnClose;
                    break;
                case IB.OrderType.Stop:                
                    oqordertype = OQ.OrderType.Stop;
                    break;
                case IB.OrderType.StopLimit:
                    oqordertype = OQ.OrderType.StopLimit;
                    break;
                case IB.OrderType.TrailingStop:
                    oqordertype = OQ.OrderType.Trail;
                    break;
                case IB.OrderType.TrailingStopLimit:
                    oqordertype = OQ.OrderType.TrailLimit;
                    break;
                default:
                case IB.OrderType.Volatility:
                case IB.OrderType.VolumeWeightedAveragePrice:
                case IB.OrderType.Scale:
                case IB.OrderType.Relative:
                case IB.OrderType.PeggedToMarket:
                case IB.OrderType.Default:
                case IB.OrderType.Empty:
                    return false;
            }
            return true;
        }

        void ibclient_OpenOrder(object sender, OpenOrderEventArgs e)
        {
            try
            {
                ibclient_OpenOrder2(sender, e);
            }
            catch (Exception ex)
            {
                error("ibclient_OpenOrder2: " + ex.Message);
                return;
            }
        }

	    void ibclient_OpenOrder2(object sender0, OpenOrderEventArgs e)
        {
            SrBrokerOrder oqorder;
            tStart("ibclient_OpenOrder(): "
                + "Order from TWS: id: " + e.OrderId
                + ", " + e.Contract.Symbol
                + ", Status: " + e.OrderState.Status.ToString()
                + ", Shares: " + e.Order.TotalQuantity.ToString());

            lock (private_lock)
            {
                 //    lastOpenOrderUpdate = DateTime.Now;
                oqorder = new SrBrokerOrder();
                oqorder.Symbol = e.Contract.Symbol;
                oqorder.Currency = e.Contract.Currency;
                oqorder.Exchange = e.Contract.Exchange;
                if (!SecurityTypeToInstrumentType(e.Contract.SecurityType, out oqorder.InstrumentType))
                {
                    error("unknown security type in Order " + oqorder.Symbol);
                    return;
                }
                oqorder.OrderId = e.OrderId.ToString();
                oqorder.Price = (double) e.Order.LimitPrice;
                oqorder.Qty = e.Order.TotalQuantity;
                if (!ActionSideToOrderSide(e.Order.Action, out oqorder.Side))
                {
                    error("unknown action side in Order " + oqorder.Symbol);
                }
                if(!OrderStateToOrderStatus(e.OrderState.Status, out oqorder.Status))
                {
                    error("unknown status in Order " + oqorder.Symbol);
                    return;
                }
                oqorder.StopPrice = (double)e.Order.AuxPrice; // stop order only
                
                if(!OrderTypeToOrderType(e.Order.OrderType, out oqorder.Type))
                {
                    error("bad order type in Order " + oqorder.Symbol);
                }              
                oqorder.OrderId = e.OrderId.ToString();
                // Fields
                oqorder.AddCustomField("ContractId", e.Contract.ContractId.ToString());
                oqorder.AddCustomField("Multiplier", e.Contract.Multiplier);
                oqorder.AddCustomField("Account", e.Order.Account);
                oqorder.AddCustomField("ClientId", e.Order.ClientId.ToString());
                oqorder.AddCustomField("DiscretionaryAmt", e.Order.DiscretionaryAmt.ToString());
                oqorder.AddCustomField("DisplaySize", e.Order.DisplaySize.ToString());
                oqorder.AddCustomField("ETradeOnly", e.Order.ETradeOnly.ToString());
                oqorder.AddCustomField("GoodAfterTime", e.Order.GoodAfterTime);
                oqorder.AddCustomField("GoodTillDate", e.Order.GoodTillDate);
                oqorder.AddCustomField("Hidden", e.Order.Hidden.ToString());
                oqorder.AddCustomField("NbboPriceCap", e.Order.NbboPriceCap.ToString());                
                if (!string.IsNullOrWhiteSpace(e.Order.OcaGroup))
                {
                    oqorder.AddCustomField("OcaGroup", e.Order.OcaGroup);
                    oqorder.AddCustomField("OcaType", e.Order.OcaType.ToString());
                }
                oqorder.AddCustomField("OrderRef", e.Order.OrderRef);
                oqorder.AddCustomField("Origin", e.Order.Origin.ToString());
                oqorder.AddCustomField("OutsideRth", e.Order.OutsideRth.ToString());
                oqorder.AddCustomField("OverridePercentageConstraints", e.Order.OverridePercentageConstraints.ToString());
                oqorder.AddCustomField("ParentId", e.Order.ParentId.ToString());
                oqorder.AddCustomField("PercentOffset", e.Order.PercentOffset.ToString());
                oqorder.AddCustomField("PermId", e.Order.PermId.ToString());
                oqorder.AddCustomField("ReferencePriceType", e.Order.ReferencePriceType.ToString());
                oqorder.AddCustomField("Rule80A", e.Order.Rule80A.ToString());
                //oqorder.AddCustomField("ScaleInitLevelSize", e.Order.ScaleInitLevelSize.ToString());
                //oqorder.AddCustomField("ScalePriceIncrement", e.Order.ScalePriceIncrement.ToString());
                //oqorder.AddCustomField("ScaleSubsLevelSize", e.Order.ScaleSubsLevelSize.ToString());
                oqorder.AddCustomField("SettlingFirm", e.Order.SettlingFirm);
                oqorder.AddCustomField("StartingPrice", e.Order.StartingPrice.ToString());
                oqorder.AddCustomField("StockRangeLower", e.Order.StockRangeLower.ToString());
                oqorder.AddCustomField("StockRangeUpper", e.Order.StockRangeUpper.ToString());
                oqorder.AddCustomField("StockRefPrice", e.Order.StockRefPrice.ToString());
                oqorder.AddCustomField("SweepToFill", e.Order.SweepToFill.ToString());
                oqorder.AddCustomField("Tif", e.Order.Tif.ToString());
                oqorder.AddCustomField("TrailStopPrice", e.Order.TrailStopPrice.ToString());
                oqorder.AddCustomField("TriggerMethod", e.Order.TriggerMethod.ToString());
                oqorder.AddCustomField("WhatIf", e.Order.WhatIf.ToString());

                string key = oqorder.OrderId;

                //if (pendingOrders.ContainsKey(key)) 
                pendingOrders[key] = oqorder;

                //pendingOrders.Add(key, oqorder);
            }

            //reorder.OrderState = BrokerOrderState.Invalid;
            //reorder.SubmittedDate = DateTime.Now
            //    + TimeSpan.FromHours(settings.BarTimeStampCorrection);

            ////info("OpenOrder: unknown order from TWS (added): id: " + e.OrderId
            ////+ ", Status: " + e.OrderState.Status.ToString()
            ////+ ", Shares: " + e.Order.TotalQuantity.ToString());           

            //if (reorder.OrderState == BrokerOrderState.Filled
            //    || reorder.OrderState == BrokerOrderState.PartiallyFilled
            //    || reorder.OrderState == BrokerOrderState.Submitted)
            //{
            //    Console.WriteLine("Order " + e.OrderId + " known state -> IGNORED");
            //    return;
            //}

            //string information = "received from TWS";

            //switch (e.Order.Tif)
            //{
            //    case TimeInForce.Day:
            //        reorder.GoodTillCanceled = false;
            //        break;
            //    default:
            //    case TimeInForce.GoodTillCancel:
            //        reorder.GoodTillCanceled = true;
            //        break;
            //}
            //reorder.LimitPrice = (double)e.Order.LimitPrice;

            //switch (e.OrderState.Status)
            //{

            //    case OrderStatus.Inactive: //  "Inactive":
            //        // ignore
            //        return;
            //    case OrderStatus.PendingSubmit: //   "PendingSubmit":
            //    case OrderStatus.PreSubmitted: //  "PreSubmitted":
            //    case OrderStatus.Submitted: //  "Submitted":
            //    case OrderStatus.ApiPending:
            //        reorder.OrderState = BrokerOrderState.Submitted;
            //        break;
            //    case OrderStatus.Filled: // "Filled":
            //        reorder.OrderState = BrokerOrderState.Filled;
            //        break;
            //    case OrderStatus.PartiallyFilled: // "PartiallyFilled":
            //        reorder.OrderState = BrokerOrderState.PartiallyFilled;
            //        break;
            //    case OrderStatus.PendingCancel: // "PendingCancel":
            //        reorder.OrderState = BrokerOrderState.PendingCancel;
            //        break;
            //    case OrderStatus.Canceled: //  "Cancelled":
            //    case OrderStatus.ApiCancelled:
            //        reorder.OrderState = BrokerOrderState.Cancelled;
            //        break;
            //    default:
            //        error("unknown order state: " + e.OrderState.Status);
            //        return;
            //}

            ////TODO Market on Open
            //reorder.Shares = e.Order.TotalQuantity;
            //reorder.StopPrice = (double)e.Order.LimitPrice; // if stop order
            //// reorder.SubmittedDate
            //// reorder.TrailingStop


            //logger.AddLog(LoggerLevel.Information, "ibclient_OpenOrder: "
            //    + " Order form from TWS: " + reorder.ToString());

            //lock (private_lock)
            //{
            //    if (!openOrders.ContainsKey(e.OrderId.ToString()))
            //        openOrders.Add(e.OrderId.ToString(), new SrOrder(reorder));
            //    else openOrders[e.OrderId.ToString()].reorder = reorder;
            //}

            //// wait until RE is ready to receive Order Updates
            //if (reConnected)
            //{
            //    SendOrderToRE(reorder, information);

            //}
            tEnd("ibclient_OpenOrder()");
        }

        void ibclient_ConnectionClosed(object sender, ConnectionClosedEventArgs e)
        {
            tStart("ibclient_ConnectionClosed()");
            //Console.WriteLine("Stingray: Connection to TWS closed");
            logger.AddLog(LoggerLevel.Error, "ConnectionClosed()");
            lock (private_lock)
            {
                try
                {
                    if (ibclient != null && ibclient.Connected)
                        ibclient.Disconnect();
                }
                catch (Exception /*ex*/)
                {
                    // MessageBox.Show("ibclient_ConnectionClosed: Disconnect: " + ex.Message);
                    ibclient = null;
                }
                ordersValid = false;
                positionsValid = false;
            }
            _isConnected = false;
            EmitDisconnected();
            tEnd("ibclient_ConnectionClosed()");
        }

        void ibclient_NextValidId(object sender, NextValidIdEventArgs e)
        {
            tStart("ibclient_NextValidId(): ID= "
                + e.OrderId.ToString()
                + " waiting for private_lock");
            lock (private_lock)
            {
                if (e.OrderId > nextValidId) nextValidId = e.OrderId;
            }
            tEnd("ibclient_NextValidId()");
        }

        private void CancelOQOrder(string orderId, string errorMsg)
        {
            tStart("CancelOQOrder: err=" + errorMsg);

            lock (private_lock)
            {
                if (!workingOrders.ContainsKey(orderId))
                {
                    error("CancelOQOrder: no working order with Id " + orderId + " found");
                    return;
                }
                OQ.Order oqorder = workingOrders[orderId];
                string text = errorMsg + ": " + oqorder.Text;

                oqorder.Text = text;
                // EmitCancelled(oqorder);
                EmitRejected(oqorder, text);
            }
            tEnd("CancelOQOrder");
        }

	    void ibclient_Error(object sender, Krs.Ats.IBNet96.ErrorEventArgs e)
        {
            tStart("ibclient_Error() " + e.ErrorMsg);
            string orderId = e.TickerId.ToString();

            switch ((int)e.ErrorCode)
            {
                case 135: // "Can't find Order"
                    // cancel this order in OQ
                    logger.AddLog(LoggerLevel.Information, "Order " + e.TickerId.ToString()
                        + " not known in TWS, cancel in OQ: "
                        + e.ErrorMsg);
                   
                    CancelOQOrder(orderId, e.ErrorMsg);

                    //lock (private_lock)
                    //{
                    //    // find matching order
                    //    if (openOrders.ContainsKey(orderId))
                    //    {
                    //        oqorder = openOrders[orderId].oqorder;
                    //        //oqorder.OrderState = BrokerOrderState.Cancelled;
                    //    }
                    //}
                    //string information = "Error from TWS: " + e.ErrorMsg;
                    //// CancelREOrder(reorder, information);
                    //lock (private_lock)
                    //{
                    //    if (openOrders.ContainsKey(orderId))
                    //    {
                    //        openOrders.Remove(orderId);
                    //    }
                    //}
                    break;
                case 202: // Order Cancelled
                    if (e.ErrorMsg.EndsWith("Canceled - reason:"))
                    {
                        logger.AddLog(LoggerLevel.Detail, "TWS Error: " + e.ErrorMsg);
                        CancelOQOrder(orderId, e.ErrorMsg);
                        return;
                    }
                    break;
                case 300: // can't find Eid with tickerID: N
                    // we canceled a Mkt data request which was not active
                    logger.AddLog(LoggerLevel.Detail, "TWS Error: " + e.ErrorMsg);
                    return; // ignore
                case 1100: // Connectivity between IB and TWS has been lost                
                    error(e.ErrorMsg); // write error out
                    logger.AddLog(LoggerLevel.Error, "TWS Error: " + e.ErrorMsg);
                    return; // and ignore
                case 1102: // Connectivity between IB and TWS has been restored - data maintained                
                    info(e.ErrorMsg); // write info out
                    logger.AddLog(LoggerLevel.Detail, "TWS Error: " + e.ErrorMsg);
                    return; // and ignore
                case 2100: // new account data requested
                case 2103: // Market data farm connection is broken
                case 2104: // Market Data Farm Connection ok
                case 2105:
                case 2106: // Market data farm connection is ok
                case 2107: // HMDS data farm connection is inactive but should be available...
                    // ignore
                    logger.AddLog(LoggerLevel.Detail, "TWS Error: " + e.ErrorMsg);
                    return;
                default:
                    if(workingOrders.ContainsKey(orderId))
                    {                        
                        CancelOQOrder(orderId, e.ErrorMsg);
                    }
                    break;
            }
            string code;
            if (((int)e.ErrorCode).ToString() != e.ErrorCode.ToString())
            {
                code = "(code="
                + ((int)e.ErrorCode).ToString() + " = " + e.ErrorCode.ToString()
                + ")";
            }
            else
            {
                code = "(code=" + e.ErrorCode.ToString() + ")";
            }
            string text = "Stingray: Error: " + code + ": " + e.ErrorMsg;
            if (e.TickerId >= 0 && e.TickerId < int.MaxValue)
            {
                text += ", TickerId: " + e.TickerId.ToString();
                //if (tickInfoDict.ContainsKey(e.TickerId))
                //{
                //    string symbol = tickInfoDict[e.TickerId].symbol.Name;
                //    text += ", Symbol: " + symbol;
                //}
            }
            error(text);
            tEnd("ibclient_Error()");
        }

        void ibclient_CurrentTime(object sender, CurrentTimeEventArgs e)
        {
            tStart("ibclient_CurrentTime()");
            lock (private_lock)
            {
                serverTime = e.Time; // GMT/ UTC
            }
            info("CurrentTime: " + serverTime.ToString() + " UTC");
            tEnd("ibclient_CurrentTime()");
        }
        #endregion

        #region private methods
        private void checkOpenOrders()
        {
            int numGood = 0;
            int numBad = 0;
            foreach (KeyValuePair<string, SrOrder> kvp in openOrders)
            {
                SrOrder sro = kvp.Value;
                if (sro.acknowledged) continue;
                if (sro.PlaceDate == DateTime.MaxValue) continue;
                if (DateTime.Now - sro.PlaceDate > TimeSpan.FromSeconds(60.0))
                {
                    //string text = "Order not acknowledged for 60 seconds: " + sro.ToString();
                    //logger.AddLog(LoggerLevel.Error,text);
                    //Console.Error.WriteLine(text);

                    // cancel orders without ack
                    // cancel in RE


                    //// cancel at IB
                    //try
                    //{
                    //    int orderId = int.Parse(kvp.Key);
                    //    ibclient.CancelOrder(orderId);
                    //}
                    //catch (Exception ex)
                    //{
                    //    error("can't cancel order: " + ex.Message);
                    //}
                    numBad++;
                }
                else numGood++;
            }
            logger.AddLog(LoggerLevel.Error, "Watchdog: "
                + openOrders.Count.ToString()
                + " orders checked, "
                + numGood.ToString()
                + " good, "
                + numBad.ToString()
                + " bad");
        }

        private void RequestAccountUpdates()
        {
            string[] acctArray = null;
            lock(private_lock)
            {
                if (activeAccounts.Count > 0)
                {
                    acctArray = new string[activeAccounts.Count];
                    activeAccounts.Keys.CopyTo(acctArray, 0);
                }
            }

            if (acctArray == null)
            {
                logger.AddLog(LoggerLevel.Detail, "Request account updates for non-FA account");
                ibclient.RequestAccountUpdates(true, "");
                Thread.Sleep(700);
            }
            else // accounts known
            {
                foreach (string acctName in acctArray)
                {                                      
                    logger.AddLog(LoggerLevel.Detail, "Request account updates for " + acctName);
                    ibclient.RequestAccountUpdates(true, acctName);
                    Thread.Sleep(700);                    
                }
            }
        }
        #endregion

        #region Diagnostics
        private string Version()
        {
            string rev = svnRevision.Replace("$", "");
            return Assembly.GetExecutingAssembly().GetName().Version.ToString()
                + " " + rev;
        }

        private void InitLogger()
        {
            // Logger
            logger = new Logger("StingrayOQ", "StingrayOQ");
            string bp = AppDomain.CurrentDomain.BaseDirectory.ToString();
            string logfile = "StingrayOQ-"
                + ClientId.ToString()
                + "-"
                + DateTime.Now.DayOfWeek.ToString()
                + ".log";
            string logpath = Path.Combine(bp, Path.Combine("bin", logfile));

            // check last modification date
            if (File.GetLastWriteTime(logpath) + TimeSpan.FromDays(3.0) < DateTime.Now)
            {
                // Remove old log file
                File.Delete(logpath);
            }
            logger.LogFilePath = logpath;
            // DEBUG
            // MessageBox.Show("log path: " + logger.LogFilePath);
        }

        private void info(string text)
        {
            logger.AddLog(LoggerLevel.Information, text);
            Console.WriteLine("StingrayOQ: " + text);           
        }

        private void error(string text)
        {
            logger.AddLog(LoggerLevel.Error, text);
            EmitError("Error: StingrayOQ: " + text);        
        }

        private void tStart(string text)
        {
            logger.AddLog(LoggerLevel.Detail, "START("
                + Thread.CurrentThread.ManagedThreadId.ToString()
                + ") "
                + text);
        }

        private void tEnd(string text)
        {
            logger.AddLog(LoggerLevel.Detail, "END  ("
                + Thread.CurrentThread.ManagedThreadId.ToString()
                + ") "
                + text);
        }
        #endregion

        #region WatchDog
        void watchdog_Tick(object sender, EventArgs e)
        {
            //tStart("watchdog_Tick()");
            if (!IsConnected) return;

            watchdog.Stop();
            try
            {
                checkOpenOrders();
            }
            catch (Exception ex)
            {
                error("watchdog: can't check orders, " + ex.Message);
            }
            watchdog.Start();

            //tEnd("watchdog_Tick()");
        }
	    #endregion

    } // class StingRay
} // namespace finantic.OQPlugins